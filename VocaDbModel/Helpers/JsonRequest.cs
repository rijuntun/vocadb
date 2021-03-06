﻿using System.IO;
using System.Net;
using Newtonsoft.Json;

namespace VocaDb.Model.Helpers {

	public class JsonRequest {

		/// <summary>
		/// Reads JSON object from URL.
		/// </summary>
		/// <typeparam name="T">Type of object to be read.</typeparam>
		/// <param name="url">URL. Cannot be null or empty.</param>
		/// <param name="timeoutMs">Request timeout in milliseconds.</param>
		/// <returns>The read object.</returns>
		/// <exception cref="WebException">If a web request error occurred.</exception>
		/// <exception cref="JsonSerializationException">If the response wasn't valid JSON.</exception>
		public static T ReadObject<T>(string url, int timeoutMs = 100000) {
			
			var request = (HttpWebRequest)WebRequest.Create(url);
			request.Timeout = timeoutMs;
			request.UserAgent = "VocaDB";

			using (var response = request.GetResponse())
			using (var stream = response.GetResponseStream())
			using (var streamReader = new StreamReader(stream))
			using (var jsonReader = new JsonTextReader(streamReader)) {
				var serializer = new JsonSerializer();
				return serializer.Deserialize<T>(jsonReader);
			}

		}

	}

}
