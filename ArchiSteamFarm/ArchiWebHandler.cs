﻿/*
    _                _      _  ____   _                           _____
   / \    _ __  ___ | |__  (_)/ ___| | |_  ___   __ _  _ __ ___  |  ___|__ _  _ __  _ __ ___
  / _ \  | '__|/ __|| '_ \ | |\___ \ | __|/ _ \ / _` || '_ ` _ \ | |_  / _` || '__|| '_ ` _ \
 / ___ \ | |  | (__ | | | || | ___) || |_|  __/| (_| || | | | | ||  _|| (_| || |   | | | | | |
/_/   \_\|_|   \___||_| |_||_||____/  \__|\___| \__,_||_| |_| |_||_|   \__,_||_|   |_| |_| |_|

 Copyright 2015-2016 Łukasz "JustArchi" Domeradzki
 Contact: JustArchi@JustArchi.net

 Licensed under the Apache License, Version 2.0 (the "License");
 you may not use this file except in compliance with the License.
 You may obtain a copy of the License at

 http://www.apache.org/licenses/LICENSE-2.0
					
 Unless required by applicable law or agreed to in writing, software
 distributed under the License is distributed on an "AS IS" BASIS,
 WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 See the License for the specific language governing permissions and
 limitations under the License.

*/
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using HtmlAgilityPack;
using SteamKit2;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Xml;

namespace ArchiSteamFarm {
	internal sealed class ArchiWebHandler {
		private const string SteamCommunity = "steamcommunity.com";

		private static string SteamCommunityURL = "https://" + SteamCommunity;

		private static int Timeout = 30 * 1000;

		private readonly Bot Bot;
		private readonly Dictionary<string, string> Cookie = new Dictionary<string, string>(4);

		private ulong SteamID;

		internal static void Init() {
			Timeout = Program.GlobalConfig.HttpTimeout * 1000;
			SteamCommunityURL = (Program.GlobalConfig.ForceHttp ? "http://" : "https://") + SteamCommunity;
		}

		internal ArchiWebHandler(Bot bot) {
			if (bot == null) {
				return;
			}

			Bot = bot;
		}

		internal async Task<bool> Init(SteamClient steamClient, string webAPIUserNonce, string parentalPin) {
			if (steamClient == null || steamClient.SteamID == null || string.IsNullOrEmpty(webAPIUserNonce)) {
				return false;
			}

			SteamID = steamClient.SteamID;

			string sessionID = Convert.ToBase64String(Encoding.UTF8.GetBytes(SteamID.ToString()));

			// Generate an AES session key
			byte[] sessionKey = CryptoHelper.GenerateRandomBlock(32);

			// RSA encrypt it with the public key for the universe we're on
			byte[] cryptedSessionKey;
			using (RSACrypto rsa = new RSACrypto(KeyDictionary.GetPublicKey(steamClient.ConnectedUniverse))) {
				cryptedSessionKey = rsa.Encrypt(sessionKey);
			}

			// Copy our login key
			byte[] loginKey = new byte[webAPIUserNonce.Length];
			Array.Copy(Encoding.ASCII.GetBytes(webAPIUserNonce), loginKey, webAPIUserNonce.Length);

			// AES encrypt the loginkey with our session key
			byte[] cryptedLoginKey = CryptoHelper.SymmetricEncrypt(loginKey, sessionKey);

			// Do the magic
			Logging.LogGenericInfo("Logging in to ISteamUserAuth...", Bot.BotName);

			KeyValue authResult;
			using (dynamic iSteamUserAuth = WebAPI.GetInterface("ISteamUserAuth")) {
				iSteamUserAuth.Timeout = Timeout;

				try {
					authResult = iSteamUserAuth.AuthenticateUser(
						steamid: SteamID,
						sessionkey: Encoding.ASCII.GetString(WebUtility.UrlEncodeToBytes(cryptedSessionKey, 0, cryptedSessionKey.Length)),
						encrypted_loginkey: Encoding.ASCII.GetString(WebUtility.UrlEncodeToBytes(cryptedLoginKey, 0, cryptedLoginKey.Length)),
						method: WebRequestMethods.Http.Post,
						secure: !Program.GlobalConfig.ForceHttp
					);
				} catch (Exception e) {
					Logging.LogGenericException(e, Bot.BotName);
					return false;
				}
			}

			if (authResult == null) {
				return false;
			}

			Logging.LogGenericInfo("Success!", Bot.BotName);

			string steamLogin = authResult["token"].AsString();
			string steamLoginSecure = authResult["tokensecure"].AsString();

			Cookie["sessionid"] = sessionID;
			Cookie["steamLogin"] = steamLogin;
			Cookie["steamLoginSecure"] = steamLoginSecure;

			// The below is used for display purposes only
			Cookie["webTradeEligibility"] = "{\"allowed\":0,\"reason\":0,\"allowed_at_time\":0,\"steamguard_required_days\":0,\"sales_this_year\":0,\"max_sales_per_year\":0,\"forms_requested\":0}";

			await UnlockParentalAccount(parentalPin).ConfigureAwait(false);
			return true;
		}

		internal async Task<bool?> IsLoggedIn() {
			if (SteamID == 0) {
				return false;
			}

			HtmlDocument htmlDocument = null;
			for (byte i = 0; i < WebBrowser.MaxRetries && htmlDocument == null; i++) {
				htmlDocument = await WebBrowser.UrlGetToHtmlDocument(SteamCommunityURL + "/my/profile", Cookie).ConfigureAwait(false);
			}

			if (htmlDocument == null) {
				Logging.LogGenericWTF("Request failed even after " + WebBrowser.MaxRetries + " tries", Bot.BotName);
				return null;
			}

			HtmlNode htmlNode = htmlDocument.DocumentNode.SelectSingleNode("//span[@id='account_pulldown']");
			return htmlNode != null;
		}

		internal async Task<bool> ReconnectIfNeeded() {
			bool? isLoggedIn = await IsLoggedIn().ConfigureAwait(false);
			if (isLoggedIn.HasValue && !isLoggedIn.Value) {
				Logging.LogGenericInfo("Reconnecting because our sessionID expired!", Bot.BotName);
				Task.Run(async () => await Bot.Restart().ConfigureAwait(false)).Forget();
				return true;
			}

			return false;
		}

		internal async Task<Dictionary<uint, string>> GetOwnedGames() {
			if (SteamID == 0) {
				return null;
			}

			string request = SteamCommunityURL + "/profiles/" + SteamID + "/games/?xml=1";

			XmlDocument response = null;
			for (byte i = 0; i < WebBrowser.MaxRetries && response == null; i++) {
				response = await WebBrowser.UrlGetToXML(request, Cookie).ConfigureAwait(false);
			}

			if (response == null) {
				Logging.LogGenericWTF("Request failed even after " + WebBrowser.MaxRetries + " tries", Bot.BotName);
				return null;
			}

			XmlNodeList xmlNodeList = response.SelectNodes("gamesList/games/game");
			if (xmlNodeList == null || xmlNodeList.Count == 0) {
				return null;
			}

			Dictionary<uint, string> result = new Dictionary<uint, string>(xmlNodeList.Count);
			foreach (XmlNode xmlNode in xmlNodeList) {
				XmlNode appNode = xmlNode.SelectSingleNode("appID");
				if (appNode == null) {
					continue;
				}

				uint appID;
				if (!uint.TryParse(appNode.InnerText, out appID)) {
					continue;
				}

				XmlNode nameNode = xmlNode.SelectSingleNode("name");
				if (nameNode == null) {
					continue;
				}

				result[appID] = nameNode.InnerText;
			}

			return result;
		}

		internal List<Steam.TradeOffer> GetTradeOffers() {
			if (string.IsNullOrEmpty(Bot.BotConfig.SteamApiKey)) {
				return null;
			}

			KeyValue response = null;
			using (dynamic iEconService = WebAPI.GetInterface("IEconService", Bot.BotConfig.SteamApiKey)) {
				iEconService.Timeout = Timeout;

				for (byte i = 0; i < WebBrowser.MaxRetries && response == null; i++) {
					try {
						response = iEconService.GetTradeOffers(
							get_received_offers: 1,
							active_only: 1,
							secure: !Program.GlobalConfig.ForceHttp
						);
					} catch (Exception e) {
						Logging.LogGenericException(e, Bot.BotName);
					}
				}
			}

			if (response == null) {
				Logging.LogGenericWTF("Request failed even after " + WebBrowser.MaxRetries + " tries", Bot.BotName);
				return null;
			}

			List<Steam.TradeOffer> result = new List<Steam.TradeOffer>();
			foreach (KeyValue trade in response["trade_offers_received"].Children) {
				Steam.TradeOffer tradeOffer = new Steam.TradeOffer {
					tradeofferid = trade["tradeofferid"].AsString(),
					accountid_other = (uint) trade["accountid_other"].AsUnsignedLong(), // TODO: Correct this when SK2 with https://github.com/SteamRE/SteamKit/pull/255 gets released
					trade_offer_state = trade["trade_offer_state"].AsEnum<Steam.TradeOffer.ETradeOfferState>()
				};
				foreach (KeyValue item in trade["items_to_give"].Children) {
					tradeOffer.items_to_give.Add(new Steam.Item {
						appid = item["appid"].AsString(),
						contextid = item["contextid"].AsString(),
						assetid = item["assetid"].AsString(),
						classid = item["classid"].AsString(),
						instanceid = item["instanceid"].AsString(),
						amount = item["amount"].AsString(),
					});
				}
				foreach (KeyValue item in trade["items_to_receive"].Children) {
					tradeOffer.items_to_receive.Add(new Steam.Item {
						appid = item["appid"].AsString(),
						contextid = item["contextid"].AsString(),
						assetid = item["assetid"].AsString(),
						classid = item["classid"].AsString(),
						instanceid = item["instanceid"].AsString(),
						amount = item["amount"].AsString(),
					});
				}
				result.Add(tradeOffer);
			}

			return result;
		}

		internal async Task<bool> JoinClan(ulong clanID) {
			if (clanID == 0) {
				return false;
			}

			string sessionID;
			if (!Cookie.TryGetValue("sessionid", out sessionID)) {
				return false;
			}

			string request = SteamCommunityURL + "/gid/" + clanID;

			Dictionary<string, string> data = new Dictionary<string, string>(2) {
				{"sessionID", sessionID},
				{"action", "join"}
			};

			HttpResponseMessage response = null;
			for (byte i = 0; i < WebBrowser.MaxRetries && response == null; i++) {
				response = await WebBrowser.UrlPost(request, data, Cookie).ConfigureAwait(false);
			}

			if (response == null) {
				Logging.LogGenericWTF("Request failed even after " + WebBrowser.MaxRetries + " tries", Bot.BotName);
				return false;
			}

			return true;
		}

		internal async Task<bool> AcceptTradeOffer(ulong tradeID) {
			if (tradeID == 0) {
				return false;
			}

			string sessionID;
			if (!Cookie.TryGetValue("sessionid", out sessionID)) {
				return false;
			}

			string referer = SteamCommunityURL + "/tradeoffer/" + tradeID;
			string request = referer + "/accept";

			Dictionary<string, string> data = new Dictionary<string, string>(3) {
				{"sessionid", sessionID},
				{"serverid", "1"},
				{"tradeofferid", tradeID.ToString()}
			};

			HttpResponseMessage response = null;
			for (byte i = 0; i < WebBrowser.MaxRetries && response == null; i++) {
				response = await WebBrowser.UrlPost(request, data, Cookie, referer).ConfigureAwait(false);
			}

			if (response == null) {
				Logging.LogGenericWTF("Request failed even after " + WebBrowser.MaxRetries + " tries", Bot.BotName);
				return false;
			}

			return true;
		}

		internal bool DeclineTradeOffer(ulong tradeID) {
			if (tradeID == 0 || string.IsNullOrEmpty(Bot.BotConfig.SteamApiKey)) {
				return false;
			}

			KeyValue response = null;
			using (dynamic iEconService = WebAPI.GetInterface("IEconService", Bot.BotConfig.SteamApiKey)) {
				iEconService.Timeout = Timeout;

				for (byte i = 0; i < WebBrowser.MaxRetries && response == null; i++) {
					try {
						response = iEconService.DeclineTradeOffer(
							tradeofferid: tradeID.ToString(),
							method: WebRequestMethods.Http.Post,
							secure: !Program.GlobalConfig.ForceHttp
						);
					} catch (Exception e) {
						Logging.LogGenericException(e, Bot.BotName);
					}
				}
			}

			if (response == null) {
				Logging.LogGenericWTF("Request failed even after " + WebBrowser.MaxRetries + " tries", Bot.BotName);
				return false;
			}

			return true;
		}

		internal async Task<List<Steam.Item>> GetMyTradableInventory() {
			JObject jObject = null;
			for (byte i = 0; i < WebBrowser.MaxRetries && jObject == null; i++) {
				jObject = await WebBrowser.UrlGetToJObject(SteamCommunityURL + "/my/inventory/json/753/6?trading=1", Cookie).ConfigureAwait(false);
			}

			if (jObject == null) {
				Logging.LogGenericWTF("Request failed even after " + WebBrowser.MaxRetries + " tries", Bot.BotName);
				return null;
			}

			IEnumerable<JToken> jTokens = jObject.SelectTokens("$.rgInventory.*");
			if (jTokens == null) {
				Logging.LogNullError("jTokens", Bot.BotName);
				return null;
			}

			List<Steam.Item> result = new List<Steam.Item>();
			foreach (JToken jToken in jTokens) {
				try {
					result.Add(JsonConvert.DeserializeObject<Steam.Item>(jToken.ToString()));
				} catch (Exception e) {
					Logging.LogGenericException(e, Bot.BotName);
				}
			}

			return result;
		}

		internal async Task<bool> SendTradeOffer(List<Steam.Item> inventory, ulong partnerID, string token = null) {
			if (inventory == null || inventory.Count == 0 || partnerID == 0) {
				return false;
			}

			string sessionID;
			if (!Cookie.TryGetValue("sessionid", out sessionID)) {
				return false;
			}

			List<Steam.TradeOfferRequest> trades = new List<Steam.TradeOfferRequest>(1 + inventory.Count / Trading.MaxItemsPerTrade);

			Steam.TradeOfferRequest singleTrade = null;
			for (ushort i = 0; i < inventory.Count; i++) {
				if (i % Trading.MaxItemsPerTrade == 0) {
					if (trades.Count >= Trading.MaxTradesPerAccount) {
						break;
					}

					singleTrade = new Steam.TradeOfferRequest();
					trades.Add(singleTrade);
				}

				Steam.Item item = inventory[i];
				singleTrade.me.assets.Add(new Steam.Item() {
					appid = "753",
					contextid = "6",
					amount = item.amount,
					assetid = item.id
				});
			}

			string referer = SteamCommunityURL + "/tradeoffer/new";
			string request = referer + "/send";

			foreach (Steam.TradeOfferRequest trade in trades) {
				Dictionary<string, string> data = new Dictionary<string, string>(6) {
					{"sessionid", sessionID},
					{"serverid", "1"},
					{"partner", partnerID.ToString()},
					{"tradeoffermessage", "Sent by ASF"},
					{"json_tradeoffer", JsonConvert.SerializeObject(trade)},
					{"trade_offer_create_params", string.IsNullOrEmpty(token) ? "" : $"{{\"trade_offer_access_token\":\"{token}\"}}"}
				};

				HttpResponseMessage response = null;
				for (byte i = 0; i < WebBrowser.MaxRetries && response == null; i++) {
					response = await WebBrowser.UrlPost(request, data, Cookie, referer).ConfigureAwait(false);
				}

				if (response == null) {
					Logging.LogGenericWTF("Request failed even after " + WebBrowser.MaxRetries + " tries", Bot.BotName);
					return false;
				}
			}

			return true;
		}

		internal async Task<HtmlDocument> GetBadgePage(byte page) {
			if (page == 0 || SteamID == 0) {
				return null;
			}

			HtmlDocument htmlDocument = null;
			for (byte i = 0; i < WebBrowser.MaxRetries && htmlDocument == null; i++) {
				htmlDocument = await WebBrowser.UrlGetToHtmlDocument(SteamCommunityURL + "/profiles/" + SteamID + "/badges?l=english&p=" + page, Cookie).ConfigureAwait(false);
			}

			if (htmlDocument == null) {
				Logging.LogGenericWTF("Request failed even after " + WebBrowser.MaxRetries + " tries", Bot.BotName);
				return null;
			}

			return htmlDocument;
		}

		internal async Task<HtmlDocument> GetGameCardsPage(ulong appID) {
			if (appID == 0 || SteamID == 0) {
				return null;
			}

			HtmlDocument htmlDocument = null;
			for (byte i = 0; i < WebBrowser.MaxRetries && htmlDocument == null; i++) {
				htmlDocument = await WebBrowser.UrlGetToHtmlDocument(SteamCommunityURL + "/profiles/" + SteamID + "/gamecards/" + appID + "?l=english", Cookie).ConfigureAwait(false);
			}

			if (htmlDocument == null) {
				Logging.LogGenericWTF("Request failed even after " + WebBrowser.MaxRetries + " tries", Bot.BotName);
				return null;
			}

			return htmlDocument;
		}

		internal async Task<bool> MarkInventory() {
			if (SteamID == 0) {
				return false;
			}

			HttpResponseMessage response = null;
			for (byte i = 0; i < WebBrowser.MaxRetries && response == null; i++) {
				response = await WebBrowser.UrlGet(SteamCommunityURL + "/profiles/" + SteamID + "/inventory", Cookie).ConfigureAwait(false);
			}

			if (response == null) {
				Logging.LogGenericWTF("Request failed even after " + WebBrowser.MaxRetries + " tries", Bot.BotName);
				return false;
			}

			return true;
		}

		private async Task UnlockParentalAccount(string parentalPin) {
			if (string.IsNullOrEmpty(parentalPin) || parentalPin.Equals("0")) {
				return;
			}

			Logging.LogGenericInfo("Unlocking parental account...", Bot.BotName);
			Dictionary<string, string> data = new Dictionary<string, string>(1) {
				{ "pin", parentalPin }
			};

			string referer = SteamCommunityURL;
			string request = referer + "/parental/ajaxunlock";

			HttpResponseMessage response = null;
			for (byte i = 0; i < WebBrowser.MaxRetries && response == null; i++) {
				response = await WebBrowser.UrlPost(request, data, Cookie, referer).ConfigureAwait(false);
			}

			if (response == null) {
				Logging.LogGenericWTF("Request failed even after " + WebBrowser.MaxRetries + " tries", Bot.BotName);
				return;
			}

			IEnumerable<string> setCookieValues;
			if (!response.Headers.TryGetValues("Set-Cookie", out setCookieValues)) {
				Logging.LogNullError("setCookieValues", Bot.BotName);
				return;
			}

			foreach (string setCookieValue in setCookieValues) {
				if (!setCookieValue.Contains("steamparental=")) {
					continue;
				}

				string setCookie = setCookieValue.Substring(setCookieValue.IndexOf("steamparental=", StringComparison.Ordinal) + 14);

				int index = setCookie.IndexOf(';');
				if (index > 0) {
					setCookie = setCookie.Substring(0, index);
				}

				Cookie["steamparental"] = setCookie;
				Logging.LogGenericInfo("Success!", Bot.BotName);
				return;
			}

			Logging.LogGenericWarning("Failed to unlock parental account!", Bot.BotName);
		}
	}
}
