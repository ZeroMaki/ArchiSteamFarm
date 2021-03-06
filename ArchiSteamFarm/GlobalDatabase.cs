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
using System;
using System.IO;

namespace ArchiSteamFarm {
	internal sealed class GlobalDatabase {
		private static readonly string FilePath = Path.Combine(Program.ConfigDirectory, Program.GlobalDatabaseFile);

		internal uint CellID {
			get {
				return _CellID;
			}
			set {
				if (_CellID == value) {
					return;
				}

				_CellID = value;
				Save();
			}
		}

		[JsonProperty(Required = Required.DisallowNull)]
		private uint _CellID = 0;

		internal static GlobalDatabase Load() {
			if (!File.Exists(FilePath)) {
				return new GlobalDatabase();
			}

			GlobalDatabase globalDatabase;
			try {
				globalDatabase = JsonConvert.DeserializeObject<GlobalDatabase>(File.ReadAllText(FilePath));
			} catch (Exception e) {
				Logging.LogGenericException(e);
				return null;
			}

			return globalDatabase;
		}

		// This constructor is used only by deserializer
		private GlobalDatabase() { }

		private void Save() {
			lock (FilePath) {
				try {
					File.WriteAllText(FilePath, JsonConvert.SerializeObject(this));
				} catch (Exception e) {
					Logging.LogGenericException(e);
				}
			}
		}
	}
}
