using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Serialization;

namespace Fierhub.Service.Library.Model
{
    /// <summary>
    /// Configuration model for Kafka server details.
    /// </summary>
    public class KafkaServerDetailModel
    {
        public string BootstrapServers { get; set; }
        public bool? EnableAutoCommit { get; set; }
    }

    public class FierHubConfig
    {
        public string Token { get; set; }
        public TokenRequestBody JwtSecret { get; set; }
        public AuthorizeModel Authorize { get; set; } = new AuthorizeModel();
        public Dictionary<string, string> ConnectionStrings { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public KafkaServerDetailModel KafkaServerDetail { get; set; }
        public bool IsReady { get; set; } = false;

        public Dictionary<string, string> AdditionalDetails { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        [JsonIgnore]
        public Dictionary<string, string> AdditionalConfiguration
        {
            get => AdditionalDetails;
            set => AdditionalDetails = value != null
                ? new Dictionary<string, string>(value, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        [JsonExtensionData]
        private IDictionary<string, JToken> _extensionData = new Dictionary<string, JToken>(StringComparer.OrdinalIgnoreCase);

        [OnDeserialized]
        internal void OnDeserializedMethod(StreamingContext context)
        {
            if (_extensionData != null)
            {
                foreach (var kvp in _extensionData)
                {
                    if (!AdditionalDetails.ContainsKey(kvp.Key) && !IsStandardProperty(kvp.Key))
                    {
                        AdditionalDetails[kvp.Key] = kvp.Value != null
                            ? (kvp.Value.Type == JTokenType.String ? kvp.Value.ToString() : kvp.Value.ToString(Formatting.None))
                            : null;
                    }
                }
            }
        }

        [JsonIgnore]
        private readonly ConcurrentDictionary<string, object> _sectionCache = new ConcurrentDictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        private static readonly object _lock = new object();
        private static FierHubConfig _instance;

        public static FierHubConfig Instance()
        {
            if (_instance == null)
            {
                lock (_lock)
                {
                    if (_instance == null)
                    {
                        _instance = new FierHubConfig();
                    }
                }
            }

            return _instance;
        }

        public static void SetInstance(FierHubConfig instance)
        {
            lock (_lock)
            {
                _instance = instance;
            }
        }

        public static void ResetInstanceForTesting()
        {
            lock (_lock)
            {
                _instance = null;
            }
        }

        public class KeyValueRecords
        {
            public string Key { get; set; }
            public string Value { get; set; }
        }

        public class AuthorizeModel
        {
            public List<string> Routes { get; set; } = new List<string>();
        }

        public static readonly HashSet<string> StandardProperties = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            nameof(Token),
            nameof(JwtSecret),
            nameof(Authorize),
            nameof(ConnectionStrings),
            nameof(KafkaServerDetail),
            nameof(IsReady),
            nameof(AdditionalDetails),
            nameof(AdditionalConfiguration)
        };

        public string GetConnectionString(string name)
        {
            if (ConnectionStrings.ContainsKey(name))
            {
                string value = null;
                ConnectionStrings.TryGetValue(name, out value);
                return value;
            }

            return null;
        }

        public bool AddOrUpdateConnectionString(string name, string value)
        {
            bool flag = true;
            try
            {
                if (ConnectionStrings.ContainsKey(name))
                {
                    ConnectionStrings[name] = value;
                }
                else
                {
                    ConnectionStrings.Add(name, value);
                }
            }
            catch
            {
                flag = false;
            }

            return flag;
        }

        public static bool IsStandardProperty(string propertyName)
        {
            return StandardProperties.Contains(propertyName);
        }

        public string GetDetail(string key)
        {
            if (AdditionalDetails != null && AdditionalDetails.TryGetValue(key, out var val))
            {
                return val;
            }
            return null;
        }

        public T GetDetail<T>(string key, T defaultValue = default)
        {
            if (AdditionalDetails != null && AdditionalDetails.TryGetValue(key, out var val) && !string.IsNullOrWhiteSpace(val))
            {
                if (typeof(T) == typeof(string))
                {
                    return (T)(object)val;
                }
                try
                {
                    return JsonConvert.DeserializeObject<T>(val);
                }
                catch
                {
                    return defaultValue;
                }
            }
            return defaultValue;
        }

        public static T GetKafkaServerConfig<T>()
        {
            var fierHubConfig = FierHubConfig.Instance();
            if (fierHubConfig.TryGetSection<T>("KafkaServerDetail", out var result))
            {
                return result;
            }
            return default(T);
        }

        public static T GetSection<T>(string sectionPath, T defaultValue = default)
        {
            var fierHubConfig = FierHubConfig.Instance();
            if (fierHubConfig.TryGetSection<T>(sectionPath, out var result))
            {
                return result;
            }
            return defaultValue;
        }

        public bool TryGetSection<T>(string sectionPath, out T result)
        {
            if (string.IsNullOrWhiteSpace(sectionPath))
            {
                result = default;
                return false;
            }

            string cacheKey = $"{typeof(T).FullName}::{sectionPath}";
            if (_sectionCache.TryGetValue(cacheKey, out var cachedValue))
            {
                if (cachedValue is T typedVal)
                {
                    result = typedVal;
                    return true;
                }
                if (cachedValue == null && default(T) == null)
                {
                    result = default;
                    return true;
                }
            }

            // 1. Direct match in AdditionalDetails
            if (AdditionalDetails != null && AdditionalDetails.TryGetValue(sectionPath, out var jsonStr) && !string.IsNullOrWhiteSpace(jsonStr))
            {
                if (typeof(T) == typeof(string))
                {
                    result = (T)(object)jsonStr;
                    _sectionCache[cacheKey] = result;
                    return true;
                }

                try
                {
                    var deserialized = JsonConvert.DeserializeObject<T>(jsonStr);
                    if (deserialized != null)
                    {
                        _sectionCache[cacheKey] = deserialized;
                        result = deserialized;
                        return true;
                    }
                }
                catch
                {
                    // Fallthrough
                }
            }

            // 2. Check strongly typed properties on FierHubConfig
            var segments = sectionPath.Split(new[] { ':', '.' }, StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length > 0 && TryGetTokenFromProperties(segments, out JToken token) && token != null && token.Type != JTokenType.Null)
            {
                try
                {
                    T deserialized;
                    Type targetType = typeof(T);

                    if (targetType == typeof(JToken))
                    {
                        deserialized = (T)(object)token;
                    }
                    else if (targetType == typeof(JObject) && token is JObject jObj)
                    {
                        deserialized = (T)(object)jObj;
                    }
                    else if (targetType == typeof(JArray) && token is JArray jArr)
                    {
                        deserialized = (T)(object)jArr;
                    }
                    else if (targetType == typeof(string))
                    {
                        if (token is JValue jVal)
                        {
                            deserialized = (T)(object)(jVal.Value?.ToString() ?? string.Empty);
                        }
                        else
                        {
                            deserialized = (T)(object)token.ToString();
                        }
                    }
                    else
                    {
                        deserialized = token.ToObject<T>();
                    }

                    if (deserialized != null)
                    {
                        _sectionCache[cacheKey] = deserialized;
                        result = deserialized;
                        return true;
                    }
                }
                catch
                {
                    // Deserialization error
                }
            }

            result = default;
            return false;
        }

        private bool TryGetTokenFromProperties(string[] segments, out JToken token)
        {
            token = null;
            if (segments == null || segments.Length == 0) return false;

            var prop = typeof(FierHubConfig).GetProperty(segments[0],
                BindingFlags.Public |
                BindingFlags.Instance |
                BindingFlags.IgnoreCase);

            if (prop != null)
            {
                var val = prop.GetValue(this);
                if (val == null) return false;

                try
                {
                    JToken rootToken = JToken.FromObject(val);
                    if (segments.Length == 1)
                    {
                        token = rootToken;
                        return true;
                    }

                    return NavigateJToken(rootToken, segments.Skip(1).ToArray(), out token);
                }
                catch
                {
                    return false;
                }
            }

            return false;
        }

        private static bool NavigateJToken(JToken current, string[] remainingSegments, out JToken result)
        {
            result = null;
            if (current == null) return false;

            JToken cursor = current;
            foreach (var seg in remainingSegments)
            {
                if (cursor == null) return false;

                if (cursor is JObject jObj)
                {
                    JProperty matchedProp = null;
                    foreach (var prop in jObj.Properties())
                    {
                        if (string.Equals(prop.Name, seg, StringComparison.OrdinalIgnoreCase))
                        {
                            matchedProp = prop;
                            break;
                        }
                    }

                    if (matchedProp == null) return false;
                    cursor = matchedProp.Value;
                }
                else if (cursor is JArray jArr)
                {
                    if (int.TryParse(seg, out int index) && index >= 0 && index < jArr.Count)
                    {
                        cursor = jArr[index];
                    }
                    else
                    {
                        return false;
                    }
                }
                else
                {
                    return false;
                }
            }

            result = cursor;
            return true;
        }
    }
}
