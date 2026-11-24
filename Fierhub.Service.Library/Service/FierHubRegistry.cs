using Fierhub.Service.Library.IService;
using Fierhub.Service.Library.Middleware;
using Fierhub.Service.Library.Middleware.Service;
using Fierhub.Service.Library.Model;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using System.Text;

namespace Fierhub.Service.Library.Service
{
    /// <summary>
    /// Thread-safe singleton registry for FierHub services and configuration.
    /// </summary>
    public class FierHubRegistry
    {
        private static readonly object _lock = new object();
        private static FierHubRegistry _instance;

        private readonly WebApplicationBuilder _builder;
        private readonly ILogger<FierHubRegistry> _logger;
        private readonly FierhubServiceRequest _httpServiceRequest;

        public static FierHubRegistry Init(WebApplicationBuilder builder)
        {
            if (_instance == null)
            {
                lock (_lock)
                {
                    if (_instance == null)
                    {
                        _instance = new FierHubRegistry(builder);
                    }
                }
            }
            return _instance;
        }

        public static FierHubRegistry Instance => _instance;

        public static void ResetInstanceForTesting()
        {
            lock (_lock)
            {
                _instance = null;
            }
        }

        public FierHubConfig Config => FierHubConfig.Instance();

        public FierHubRegistry RegisterFierAuth()
        {
            return this;
        }

        private FierHubRegistry(WebApplicationBuilder builder)
        {
            _builder = builder ?? throw new ArgumentNullException(nameof(builder));

            _builder.Services.AddHttpClient();
            _builder.Services.TryAddSingleton<IHttpContextAccessor, HttpContextAccessor>();
            _builder.Services.TryAddScoped<UserSession>();
            _builder.Services.TryAddSingleton<RouteValidator>();
            _builder.Services.TryAddScoped<FierhubGatewayFilter>();
            _builder.Services.TryAddScoped<FierhubServiceFilter>();
            _builder.Services.TryAddScoped<FierhubCommonService>();

            _builder.Services.TryAddSingleton<FierhubServiceRequest>();
            _builder.Services.TryAddSingleton(x => FierHubConfig.Instance());

            _builder.Services.TryAddScoped<IFierHubService>(x =>
            {
                var httpServiceRequest = x.GetRequiredService<FierhubServiceRequest>();
                var fierHubConfig = x.GetRequiredService<FierHubConfig>();

                return new FierHubService(httpServiceRequest, fierHubConfig);
            });

            var serviceProvider = _builder.Services.BuildServiceProvider();
            _logger = serviceProvider.GetService<ILogger<FierHubRegistry>>()
                      ?? LoggerFactory.Create(b => b.AddConsole()).CreateLogger<FierHubRegistry>();
            _httpServiceRequest = serviceProvider.GetRequiredService<FierhubServiceRequest>();

            _logger.LogInformation("[FierHub] Initializing FierHubRegistry singleton instance...");

            ConfigureFierhub();
        }

        private void ConfigureFierhub()
        {
            _logger.LogInformation("[FierHub] Reading application configuration for FierHub settings...");

            string fierhubToken = _builder.Configuration["FierHub:Token"]
                ?? _builder.Configuration.GetSection("FierHub")?["Token"]
                ?? _builder.Configuration.GetSection("FireHub")?["Token"];

            if (string.IsNullOrWhiteSpace(fierhubToken))
            {
                var localConfig = _builder.Configuration.GetSection("FierHub").Get<FierHubConfig>();
                if (localConfig != null && !string.IsNullOrWhiteSpace(localConfig.Token))
                {
                    fierhubToken = localConfig.Token;
                }
            }

            if (!string.IsNullOrWhiteSpace(fierhubToken))
            {
                _logger.LogInformation("[FierHub] FierHub token found in configuration. Triggering download and FierHubConfig initialization...");
                try
                {
                    ReadConfigurationFile(fierhubToken);
                    _logger.LogInformation("[FierHub] Configuration download and FierHubConfig creation completed successfully.");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[FierHub] Failed to download or initialize FierHubConfig during singleton creation: {Message}", ex.Message);
                    throw;
                }
            }
            else
            {
                _logger.LogWarning("[FierHub] No FierHub token found in configuration during FierHubRegistry creation. FierHubConfig singleton initialized with default state.");
            }
        }

        public FierHubConfig ReadConfigurationFile(string fierhubToken)
        {
            return ReadConfigurationFileAsync(fierhubToken).ConfigureAwait(false).GetAwaiter().GetResult();
        }

        public async Task<FierHubConfig> ReadConfigurationFileAsync(string fierhubToken)
        {
            if (string.IsNullOrWhiteSpace(fierhubToken))
            {
                _logger?.LogError("[FierHub] ReadConfigurationFile aborted: FierhubToken is null or whitespace.");
                throw new ArgumentException("FierhubToken cannot be null or empty.", nameof(fierhubToken));
            }

            _logger?.LogInformation("[FierHub] ReadConfigurationFile started for token: {TokenPreview}...",
                fierhubToken.Length > 8 ? fierhubToken.Substring(0, 4) + "****" : "****");

            var payload = new
            {
                accessToken = fierhubToken
            };

            const string endpoint = "https://www.fierhub.com/api/fileContent/readConfigurationFile";
            _logger?.LogInformation("[FierHub] Sending POST request to {Endpoint}...", endpoint);

            var serviceRequest = _httpServiceRequest;
            if (serviceRequest == null)
            {
                var sp = _builder?.Services.BuildServiceProvider();
                serviceRequest = sp?.GetService<FierhubServiceRequest>();
                if (serviceRequest == null)
                {
                    throw new InvalidOperationException("FierhubServiceRequest is not available.");
                }
            }

            var responseModel = await serviceRequest.PostRequestAsync<ResponseModel>(
                endpoint,
                JsonConvert.SerializeObject(payload)
            ).ConfigureAwait(false);

            if (responseModel == null)
            {
                _logger?.LogError("[FierHub] POST request to {Endpoint} returned null response.", endpoint);
                throw new InvalidOperationException("FierHub server returned null response.");
            }

            if (responseModel.statusCode != 200)
            {
                _logger?.LogError("[FierHub] POST request failed with status code {StatusCode}. Message: {Message}",
                    responseModel.statusCode, responseModel.message);
                throw new InvalidOperationException($"Fierhub Datasource must contain files, Please register with https://www.fierhub.com register and follow the step. Status: {responseModel.statusCode}. Message: {responseModel.message}");
            }

            _logger?.LogInformation("[FierHub] Configuration successfully downloaded (Status 200). Parsing JSON payload...");

            string responseString = responseModel.responseBody is string str
                ? str
                : JsonConvert.SerializeObject(responseModel.responseBody);

            if (string.IsNullOrWhiteSpace(responseString))
            {
                _logger?.LogWarning("[FierHub] Downloaded response body is empty.");
                return FierHubConfig.Instance();
            }

            var rootObj = JObject.Parse(responseString);
            var targetObj = rootObj["FierHub"] as JObject ?? rootObj["fierhub"] as JObject ?? rootObj;

            var fierHubConfig = new FierHubConfig
            {
                Token = fierhubToken,
                IsReady = true
            };

            // 1. Authorize (Optional)
            if (targetObj["Authorize"] != null)
            {
                fierHubConfig.Authorize = targetObj["Authorize"].ToObject<FierHubConfig.AuthorizeModel>() ?? new FierHubConfig.AuthorizeModel();
                _logger?.LogInformation("[FierHub] Authorize routes configured ({Count} route(s)).",
                    fierHubConfig.Authorize?.Routes?.Count ?? 0);
            }
            else
            {
                _logger?.LogInformation("[FierHub] Authorize not present in configuration (optional).");
            }

            // 2. JwtSecret (Optional)
            if (targetObj["JwtSecret"] != null)
            {
                fierHubConfig.JwtSecret = targetObj["JwtSecret"].ToObject<TokenRequestBody>();
                if (fierHubConfig.JwtSecret != null)
                {
                    fierHubConfig.JwtSecret.IsPrimary = true;
                    _logger?.LogInformation("[FierHub] JwtSecret configured (Issuer: {Issuer}, Audiance: {Audiance}, Expiry: {Expiry}s).",
                        fierHubConfig.JwtSecret.Issuer, fierHubConfig.JwtSecret.Audiance, fierHubConfig.JwtSecret.ExpiryTimeInSeconds);
                }
            }
            else
            {
                _logger?.LogInformation("[FierHub] JwtSecret not present in configuration (optional).");
            }

            // 3. ConnectionStrings (Optional)
            if (targetObj["ConnectionStrings"] != null)
            {
                fierHubConfig.ConnectionStrings = targetObj["ConnectionStrings"].ToObject<Dictionary<string, string>>()
                    ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                _logger?.LogInformation("[FierHub] ConnectionStrings configured ({Count} item(s)).",
                    fierHubConfig.ConnectionStrings.Count);
            }
            else
            {
                _logger?.LogInformation("[FierHub] ConnectionStrings not present in configuration (optional).");
            }

            // 4. KafkaServerDetail (Optional)
            if (targetObj["KafkaServerDetail"] != null)
            {
                fierHubConfig.KafkaServerDetail = targetObj["KafkaServerDetail"].ToObject<KafkaServerDetailModel>();
                _logger?.LogInformation("[FierHub] KafkaServerDetail configured (BootstrapServers: {Servers}, EnableAutoCommit: {AutoCommit}).",
                    fierHubConfig.KafkaServerDetail?.BootstrapServers, fierHubConfig.KafkaServerDetail?.EnableAutoCommit);
            }
            else
            {
                _logger?.LogInformation("[FierHub] KafkaServerDetail not present in configuration (optional).");
            }

            // 5. Additional details (e.g. Credential, Test, etc.) outside the given details
            // Stored in Dictionary<string, string> where Key = object name, Value = string json
            foreach (var prop in targetObj.Properties())
            {
                if (!FierHubConfig.IsStandardProperty(prop.Name))
                {
                    string stringJson = prop.Value != null
                        ? (prop.Value.Type == JTokenType.String ? prop.Value.ToString() : prop.Value.ToString(Formatting.None))
                        : null;

                    fierHubConfig.AdditionalDetails[prop.Name] = stringJson;
                    _logger?.LogInformation("[FierHub] Additional detail stored in Dictionary<string, string>: '{Key}'", prop.Name);
                }
            }

            // If target was wrapped in FierHub/fierhub, capture any properties outside FierHub
            if (targetObj != rootObj)
            {
                foreach (var prop in rootObj.Properties())
                {
                    if (!string.Equals(prop.Name, "FierHub", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(prop.Name, "fierhub", StringComparison.OrdinalIgnoreCase) &&
                        !FierHubConfig.IsStandardProperty(prop.Name) &&
                        !fierHubConfig.AdditionalDetails.ContainsKey(prop.Name))
                    {
                        string stringJson = prop.Value != null
                            ? (prop.Value.Type == JTokenType.String ? prop.Value.ToString() : prop.Value.ToString(Formatting.None))
                            : null;

                        fierHubConfig.AdditionalDetails[prop.Name] = stringJson;
                        _logger?.LogInformation("[FierHub] Additional root detail stored in Dictionary<string, string>: '{Key}'", prop.Name);
                    }
                }
            }

            // Update thread-safe singleton FierHubConfig
            var singletonInstance = FierHubConfig.Instance();
            singletonInstance.Token = fierHubConfig.Token;
            singletonInstance.JwtSecret = fierHubConfig.JwtSecret;
            singletonInstance.Authorize = fierHubConfig.Authorize;
            singletonInstance.ConnectionStrings = fierHubConfig.ConnectionStrings;
            singletonInstance.KafkaServerDetail = fierHubConfig.KafkaServerDetail;
            singletonInstance.AdditionalDetails = fierHubConfig.AdditionalDetails;
            singletonInstance.IsReady = true;

            FierHubConfig.SetInstance(singletonInstance);

            // Register JWT authentication service if primary key is available
            if (singletonInstance.JwtSecret != null && !string.IsNullOrEmpty(singletonInstance.JwtSecret.Key))
            {
                var primaryKey = singletonInstance.JwtSecret.Key;
                _logger?.LogInformation("[FierHub] Registering JWT Token Service with primary signing key.");
                RegisterJWTTokenService(primaryKey);
            }

            _logger?.LogInformation("[FierHub] FierHubConfig singleton instance successfully initialized.");

            return singletonInstance;
        }

        private FierHubConfig GetConfigurationDetailFromFierhub(FierhubServiceRequest fierhubServiceRequest, string accessToken, string fileName)
        {
            var payload = new
            {
                accessToken,
                fileName
            };

            var responseModel = fierhubServiceRequest.PostRequestAsync<ResponseModel>(
                "https://www.fierhub.com/api/fileContent/getConfiguration",
                JsonConvert.SerializeObject(payload)
            ).ConfigureAwait(false).GetAwaiter().GetResult();

            var responseJson = (string)responseModel!.responseBody!;
            var fierHubConfig = JsonConvert.DeserializeObject<FierHubConfig>(responseJson)!;
            if (fierHubConfig == null)
            {
                throw new Exception("Fail to get the Fierhub configuration from server.");
            }

            return fierHubConfig;
        }

        #region JWTTOKEN_AND_JSON_CONFIGURATION

        private void RegisterJWTTokenService(string key)
        {
            _builder.Services.AddAuthentication(x =>
            {
                x.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
                x.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
            })
            .AddJwtBearer(x =>
            {
                x.SaveToken = true;
                x.RequireHttpsMetadata = false;
                x.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = false,
                    ValidateAudience = false,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)),
                    ClockSkew = TimeSpan.FromSeconds(5),
                    RoleClaimType = FierhubConstants.Roles
                };
            });
        }

        private void RegisterJsonHandler()
        {
            _builder.Services.AddControllers().AddNewtonsoftJson(options =>
            {
                options.SerializerSettings.ReferenceLoopHandling = ReferenceLoopHandling.Ignore;
                options.SerializerSettings.ContractResolver = new DefaultContractResolver
                {
                    NamingStrategy = null
                };
                options.SerializerSettings.MetadataPropertyHandling = MetadataPropertyHandling.Ignore;
            });

            _builder.Services.AddControllers()
            .AddJsonOptions(options =>
            {
                options.JsonSerializerOptions.PropertyNameCaseInsensitive = false;
            });
        }

        #endregion
    }
}
