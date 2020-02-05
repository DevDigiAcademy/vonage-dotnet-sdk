using Newtonsoft.Json;
using Nexmo.Api.Cryptography;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Nexmo.Api.Request
{
    /// <summary>
    /// Responsible for sending all Nexmo API requests that do not make use of Application authentication.
    /// For application authentication, see VersionedApiRequest.
    /// </summary>
    public static class ApiRequest
    {

        public enum AuthType
        {
            Basic,
            Bearer,
            Query
        }

        const string LOGGER_CATEGORY = "Nexmo.Api.Request.ApiRequest";

        private static string _userAgent;

        /// <summary>
        /// Sets the user agent for an HTTP request
        /// </summary>
        /// <param name="request"></param>
        /// <param name="creds"></param>
        internal static void SetUserAgent(ref HttpRequestMessage request, Credentials creds)
        {
            if (string.IsNullOrEmpty(_userAgent))
            {
#if NETSTANDARD1_6 || NETSTANDARD2_0
                // TODO: watch the next core release; may have functionality to make this cleaner
                var runtimeVersion = (System.Runtime.InteropServices.RuntimeInformation.OSDescription + System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription)
                    .Replace(" ", "")
                    .Replace("/", "")
                    .Replace(":", "")
                    .Replace(";", "")
                    .Replace("_", "")
                    ;
#else
                var runtimeVersion = System.Diagnostics.FileVersionInfo
                    .GetVersionInfo(typeof(int).Assembly.Location)
                    .ProductVersion;
#endif
                var libraryVersion = typeof(ApiRequest)
                    .GetTypeInfo()
                    .Assembly
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                    .InformationalVersion;

                _userAgent = $"nexmo-dotnet/{libraryVersion} dotnet/{runtimeVersion}";

                var appVersion = creds?.AppUserAgent ?? Configuration.Instance.Settings["appSettings:Nexmo.UserAgent"];
                if (!string.IsNullOrWhiteSpace(appVersion))
                {
                    _userAgent += $" {appVersion}";
                }
            }

            request.Headers.UserAgent.ParseAdd(_userAgent);
        }

        /// <summary>
        /// Builds a query string for a get request - if there is a security secret a signature is built for the request and added to the query string
        /// </summary>
        /// <param name="parameters"></param>
        /// <param name="creds"></param>
        /// <returns></returns>
        private static StringBuilder BuildQueryString(IDictionary<string, string> parameters, Credentials creds = null)
        {
            var apiKey = (creds?.ApiKey ?? Configuration.Instance.Settings["appSettings:Nexmo.api_key"])?.ToLower();
            var apiSecret = creds?.ApiSecret ?? Configuration.Instance.Settings["appSettings:Nexmo.api_secret"];
            var securitySecret = creds?.SecuritySecret ?? Configuration.Instance.Settings["appSettings:Nexmo.security_secret"];
            SmsSignatureGenerator.Method method;
            if (creds?.Method != null)
            {
                method = creds.Method;
            }
            else if(Enum.TryParse(Configuration.Instance.Settings["appSettings:Nexmo.signing_method"], out method))
            {
                //left blank intentionally
            }
            else
            {
                method = SmsSignatureGenerator.Method.md5hash;
            }

            var sb = new StringBuilder();
            var signature_sb = new StringBuilder();
            Action<IDictionary<string, string>, StringBuilder> buildStringFromParams = (param, strings) =>
            {
                foreach (var kvp in param)
                {
                    strings.AppendFormat("{0}={1}&", WebUtility.UrlEncode(kvp.Key), WebUtility.UrlEncode(kvp.Value));
                }
            };
            Action<IDictionary<string, string>, StringBuilder> buildSignatureStringFromParams = (param, strings) =>
            {
                foreach (var kvp in param)
                {
                    strings.AppendFormat("{0}={1}&", kvp.Key.Replace('=','_').Replace('&','_'), kvp.Value.Replace('=', '_').Replace('&', '_'));
                }
            };
            parameters.Add("api_key", apiKey);
            if (string.IsNullOrEmpty(securitySecret))
            {
                // security secret not provided, do not sign
                parameters.Add("api_secret", apiSecret);
                buildStringFromParams(parameters, sb);
                return sb;
            }
            parameters.Add("timestamp", ((int)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds).ToString(CultureInfo.InvariantCulture));
            var sortedParams = new SortedDictionary<string, string>(parameters);
            buildStringFromParams(sortedParams, sb);
            buildSignatureStringFromParams(sortedParams, signature_sb);
            var queryToSign = "&" + signature_sb.ToString();
            queryToSign = queryToSign.Remove(queryToSign.Length - 1);
            var signature = SmsSignatureGenerator.GenerateSignature(queryToSign, securitySecret, method);
            sb.AppendFormat("sig={0}", signature);
            return sb;
        }

        /// <summary>
        /// extracts parameters from an object into a dictionary
        /// </summary>
        /// <param name="parameters"></param>
        /// <returns></returns>
        internal static Dictionary<string, string> GetParameters(object parameters)
        {
            var paramType = parameters.GetType().GetTypeInfo();
            var apiParams = new Dictionary<string, string>();
            foreach (var property in paramType.GetProperties())
            {
                string jsonPropertyName = null;

                if (property.GetCustomAttributes(typeof(JsonPropertyAttribute), false).Any())
                {
                    jsonPropertyName =
                        ((JsonPropertyAttribute)property.GetCustomAttributes(typeof(JsonPropertyAttribute), false).First())
                            .PropertyName;
                }

                if (null == paramType.GetProperty(property.Name).GetValue(parameters, null)) continue;

                apiParams.Add(string.IsNullOrEmpty(jsonPropertyName) ? property.Name : jsonPropertyName,
                    paramType.GetProperty(property.Name).GetValue(parameters, null).ToString());
            }
            return apiParams;
        }

        /// <summary>
        /// Retrieves the Base URI for a given component and appends the given url to the end of it.
        /// </summary>
        /// <param name="component"></param>
        /// <param name="url"></param>
        /// <returns></returns>
        internal static Uri GetBaseUriFor(Type component, string url = null)
        {
            Uri baseUri;
            if (typeof(NumberVerify) == component
                || typeof(ApiSecret) == component
                || typeof(ApplicationV2) == component
                || typeof(Voice.Call) == component
                || typeof(Redact) == component)
            {
                baseUri = new Uri(Configuration.Instance.Settings["appSettings:Nexmo.Url.Api"]);
            }
            else
            {
                baseUri = new Uri(Configuration.Instance.Settings["appSettings:Nexmo.Url.Rest"]);
            }
            return string.IsNullOrEmpty(url) ? baseUri : new Uri(baseUri, url);
        }

        /// <summary>
        /// Creates a query string for the given parameters - if the auth type is zero the credentials are appended to the end of the query string
        /// </summary>
        /// <param name="parameters"></param>
        /// <param name="type"></param>
        /// <param name="creds"></param>
        /// <returns></returns>
        internal static StringBuilder GetQueryStringBuilderFor(object parameters, AuthType type, Credentials creds = null)
        {
            var apiParams = GetParameters(parameters);
            var sb = new StringBuilder();
            if (type == AuthType.Query)
            {
                sb = BuildQueryString(apiParams, creds);
            }
            else
            {
                foreach (var key in apiParams.Keys)
                {
                    sb.AppendFormat("{0}={1}&", WebUtility.UrlEncode(key), WebUtility.UrlEncode(apiParams[key]));
                }
            }
            
            return sb;
        }

        /// <summary>
        /// Send a GET request to the Nexmo API.
        /// Do not include credentials in the parameters object. If you need to override credentials, use the optional Credentials parameter.
        /// </summary>
        /// <param name="uri">The URI to GET</param>
        /// <param name="parameters">Parameters required by the endpoint (do not include credentials)</param>
        /// <param name="creds">(Optional) Overridden credentials for only this request</param>
        /// <exception cref="NexmoHttpRequestException">Thrown if the API encounters a non-zero result</exception>
        /// <returns></returns>
        public static T DoGetRequest<T>(Uri uri, Dictionary<string, string> parameters, Credentials creds = null)
        {
            var sb = BuildQueryString(parameters, creds);
            return DoGetRequest<T>(new Uri(uri, "?" + sb), AuthType.Query, creds);
        }

        /// <summary>
        /// Send a GET request to the versioned Nexmo API.
        /// Do not include credentials in the parameters object. If you need to override credentials, use the optional Credentials parameter.
        /// </summary>
        /// <param name="uri">The URI to GET</param>
        /// <param name="parameters">Parameters required by the endpoint (do not include credentials)</param>
        /// <param name="creds">(Optional) Overridden credentials for only this request</param>
        /// <exception cref="NexmoHttpRequestException">Thrown if the API encounters a non-zero result</exception>
        public static T DoGetRequest<T>(Uri uri, object parameters, AuthType authType, Credentials creds = null)
        {
            var sb = GetQueryStringBuilderFor(parameters, authType, creds);

            return DoGetRequest<T>(new Uri(uri, "?" + sb), authType, creds);
        }

        /// <summary>
        /// Sends an HTTP GET request to the Nexmo API without any additional parameters
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="uri"></param>
        /// <param name="authType"></param>
        /// <param name="creds"></param>
        /// <exception cref="NexmoHttpRequestException">Thrown if the API encounters a non-zero result</exception>
        public static T DoGetRequest<T>(Uri uri, AuthType authType, Credentials creds)
        {
            var logger = Logger.LogProvider.GetLogger(LOGGER_CATEGORY);
            var appId = creds?.ApplicationId ?? Configuration.Instance.Settings["appSettings:Nexmo.Application.Id"];
            var appKeyPath = creds?.ApplicationKey ?? Configuration.Instance.Settings["appSettings:Nexmo.Application.Key"];            
            var apiKey = (creds?.ApiKey ?? Configuration.Instance.Settings["appSettings:Nexmo.api_key"])?.ToLower();
            var apiSecret = creds?.ApiSecret ?? Configuration.Instance.Settings["appSettings:Nexmo.api_secret"];

            var req = new HttpRequestMessage
            {
                RequestUri = uri,
                Method = HttpMethod.Get
            };
            SetUserAgent(ref req, creds);
            
            if (authType == AuthType.Basic)
            {
                var authBytes = Encoding.UTF8.GetBytes(apiKey + ":" + apiSecret);
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic",
                    Convert.ToBase64String(authBytes));
            }
            else if (authType == AuthType.Bearer)
            {
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer",
                    Jwt.CreateToken(appId, appKeyPath));
            }
            logger.LogDebug($"GET {uri}");
            return JsonConvert.DeserializeObject<T>(SendHttpRequest(req).JsonResponse);
        }

        /// <summary>
        /// Send a request to the Nexmo API using the specified HTTP method and the provided parameters.
        /// Do not include credentials in the parameters object. If you need to override credentials, use the optional Credentials parameter.
        /// </summary>
        /// <param name="method">HTTP method (POST, PUT, DELETE, etc)</param>
        /// <param name="uri">The URI to communicate with</param>
        /// <param name="parameters">Parameters required by the endpoint (do not include credentials)</param>
        /// <param name="creds">(Optional) Overridden credentials for only this request</param>
        /// <exception cref="NexmoHttpRequestException">thrown if an error is encountered when talking to the API</exception>
        /// <returns></returns>
        public static NexmoResponse DoRequestWithUrlContent(string method, Uri uri, Dictionary<string, string> parameters, Credentials creds = null)
        {
            var logger = Logger.LogProvider.GetLogger(LOGGER_CATEGORY);
            var sb = new StringBuilder();
            // if parameters is null, assume that key and secret have been taken care of
            if (null != parameters)
            {
                sb = BuildQueryString(parameters, creds);
            }

            var req = new HttpRequestMessage
            {
                RequestUri = uri,
                Method = new HttpMethod(method)
            };
            SetUserAgent(ref req, creds);
            
            var data = Encoding.ASCII.GetBytes(sb.ToString());
            req.Content = new ByteArrayContent(data);
            req.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-www-form-urlencoded");

            logger.LogDebug($"{method} {uri} {sb}");
            return SendHttpRequest(req);
        }

        /// <summary>
        /// Sends an HttpRequest on to the Nexmo API
        /// </summary>
        /// <param name="req"></param>
        /// <exception cref="NexmoHttpRequestException">thrown if an error is encountered when talking to the API</exception>
        /// <returns></returns>
        public static NexmoResponse SendHttpRequest(HttpRequestMessage req)
        {
            var logger = Logger.LogProvider.GetLogger(LOGGER_CATEGORY);
            var response = Configuration.Instance.Client.SendAsync(req).Result;
            var stream = response.Content.ReadAsStreamAsync().Result;
            string json;
            using (var sr = new StreamReader(stream))
            {
                json = sr.ReadToEnd();
            }
            try
            {
                logger.LogDebug(json);
                response.EnsureSuccessStatusCode();
                return new NexmoResponse
                {
                    Status = response.StatusCode,
                    JsonResponse = json
                };
            }
            catch (HttpRequestException exception)
            {
                logger.LogError($"FAIL: {response.StatusCode}");
                throw new NexmoHttpRequestException(exception.Message) { HttpStatusCode = response.StatusCode, Json = json };
            }
        }

        /// <summary>
        /// Send a request to the versioned Nexmo API.
        /// Do not include credentials in the parameters object. If you need to override credentials, use the optional Credentials parameter.
        /// </summary>
        /// <param name="method">HTTP method (POST, PUT, DELETE, etc)</param>
        /// <param name="uri">The URI to communicate with</param>
        /// <param name="payload">Parameters required by the endpoint (do not include credentials)</param>
        /// <param name="authType">Authorization type used on the API</param>
        /// <param name="creds">(Optional) Overridden credentials for only this request</param>
        /// <exception cref="NexmoHttpRequestException">thrown if an error is encountered when talking to the API</exception>
        public static NexmoResponse DoRequestWithJsonContent(string method, Uri uri, object payload, AuthType authType, Credentials creds)
        {
            var appId = creds?.ApplicationId ?? Configuration.Instance.Settings["appSettings:Nexmo.Application.Id"];
            var appKeyPath = creds?.ApplicationKey ?? Configuration.Instance.Settings["appSettings:Nexmo.Application.Key"];
            var apiKey = (creds?.ApiKey ?? Configuration.Instance.Settings["appSettings:Nexmo.api_key"])?.ToLower();
            var apiSecret = creds?.ApiSecret ?? Configuration.Instance.Settings["appSettings:Nexmo.api_secret"];            

            var req = new HttpRequestMessage
            {
                RequestUri = uri,
                Method = new HttpMethod(method),
            };
            SetUserAgent(ref req, creds);
            
            if (authType == AuthType.Basic)
            {
                var authBytes = Encoding.UTF8.GetBytes(apiKey + ":" + apiSecret);
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic",
                    Convert.ToBase64String(authBytes));
            }
            else if (authType == AuthType.Bearer)
            {
                // attempt bearer token auth
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer",
                    Jwt.CreateToken(appId, appKeyPath));
            }
            else
            {
                throw new ArgumentException("Unkown Auth Type set for function");
            }

            var data = Encoding.ASCII.GetBytes(JsonConvert.SerializeObject(payload,
                Formatting.None, new JsonSerializerSettings { DefaultValueHandling = DefaultValueHandling.Ignore }));
            req.Content = new ByteArrayContent(data);
            req.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

            return SendHttpRequest(req);
        }

        /// <summary>
        /// Sends a GET request to the Nexmo API using a JWT and returns the full HTTP resonse message 
        /// this is primarily for pulling a raw stream off an API call -e.g. a recording
        /// </summary>
        /// <param name="uri"></param>
        /// <param name="creds"></param>
        /// <returns>HttpResponseMessage</returns>
        public static HttpResponseMessage DoGetRequestWithJwt(Uri uri, Credentials creds)
        {
            var logger = Logger.LogProvider.GetLogger(LOGGER_CATEGORY);
            var appId = creds?.ApplicationId ?? Configuration.Instance.Settings["appSettings:Nexmo.Application.Id"];
            var appKeyPath = creds?.ApplicationKey ?? Configuration.Instance.Settings["appSettings:Nexmo.Application.Key"];

            var req = new HttpRequestMessage
            {
                RequestUri = uri,
                Method = HttpMethod.Get
            };

            SetUserAgent(ref req, creds);

            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer",
                Jwt.CreateToken(appId, appKeyPath));

            logger.LogDebug($"GET {uri}");

            var result = Configuration.Instance.Client.SendAsync(req).Result;

            try
            {
                result.EnsureSuccessStatusCode();
                return result;
            }
            catch (HttpRequestException ex)
            {
                logger.LogError($"FAIL: {result.StatusCode}");
                throw new NexmoHttpRequestException(ex.Message, ex) { HttpStatusCode = result.StatusCode };
            }
            
        }

        /// <summary>
        /// Sends a Post request to the specified endpoint with the given parameters
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="uri"></param>
        /// <param name="parameters"></param>
        /// <param name="creds"></param>
        /// <returns></returns>
        public static T DoPostRequest<T>(Uri uri, object parameters, Credentials creds = null)
        {
            var apiParams = GetParameters(parameters);
            return DoPostRequestWithUrlContent<T>(uri, apiParams, creds);
        }

        /// <summary>
        /// Send a Post Request with Url Content
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="uri"></param>
        /// <param name="parameters"></param>
        /// <param name="creds"></param>
        /// <returns></returns>
        public static T DoPostRequestWithUrlContent<T>(Uri uri, Dictionary<string, string> parameters, Credentials creds = null) 
        {
            var response = DoRequestWithUrlContent("POST", uri, parameters, creds);
            return JsonConvert.DeserializeObject<T>(response.JsonResponse);
        }
        public static T DoPutRequestWithUrlContent<T>(Uri uri, Dictionary<string, string> parameters, Credentials creds = null) {
            var response = DoRequestWithUrlContent("PUT", uri, parameters, creds);
            return JsonConvert.DeserializeObject<T>(response.JsonResponse);
        }
        public static NexmoResponse DoDeleteRequestWithUrlContent(Uri uri, Dictionary<string, string> parameters, Credentials creds = null) => DoRequestWithUrlContent("DELETE", uri, parameters, creds);
    }
}