using Fierhub.Service.Library.Model;
using Fierhub.Service.Library.Service;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Fierhub.Service.Library.Tests
{
    public class FierHubTests
    {
        private const string SampleJson = @"{
  ""Authorize"": {
    ""Routes"": [
      ""/api/Weather"",
      ""/favicon.ico"",
      ""/bts/resources"",
      ""/Price/AddFreeTrial"",
      ""/Price/GetPriceDetail"",
      ""/Login/Authenticate"",
      ""/api/RunAccrualManually"",
      ""/api/auth/LoginConfig/ReGenerateAccessToken"",
      ""/api/auth/LoginConfig/LoginOnRefreshToken"",
      ""/Login/ForgotPassword"",
      ""/Login/ResetPassword"",
      ""/api/AutoTrigger/RunAndBuilEmployeeSalaryAndDeclaration"",
      ""/WeatherForecast""
    ]
  },
  ""JwtSecret"": {
    ""Key"": ""bottomhalfx12#@-emstum-api-servic-x#20$46@3211"",
    ""Issuer"": ""bottomhalf-services"",
    ""ExpiryTimeInSeconds"": 72000,
    ""Audiance"": ""emstum_user"",
    ""RefreshTokenExpiryTimeInSeconds"": 2592000
  },
  ""ConnectionStrings"": {
    ""master"": ""server=bt-mysql-proxysql-helm-proxysql.bt.svc.cluster.local;port=6033;database=ems_master;User Id=admin;password=Bt#Admin!2026_Kvm8x9QpLz24;Connection Timeout=30;Connection Lifetime=0;Min Pool Size=1;Max Pool Size=20;Pooling=True;""
  },
  ""KafkaServerDetail"": {
    ""BootstrapServers"": ""kafka.bt.svc.cluster.local:9092"",
    ""EnableAutoCommit"": true
  },
  ""Credential"": {
    ""Username"": ""admin_service"",
    ""Password"": ""P@ssw0rd123""
  },
  ""Test"": [
    { ""Id"": 101, ""Name"": ""Item1"" },
    { ""Id"": 102, ""Name"": ""Item2"" }
  ]
}";

        [Fact]
        public void Deserialization_ShouldPopulateGivenDetailsAndAdditionalDetails()
        {
            var config = JsonConvert.DeserializeObject<FierHubConfig>(SampleJson);

            // 1. Authorize
            Assert.NotNull(config.Authorize);
            Assert.Equal(13, config.Authorize.Routes.Count);
            Assert.Contains("/api/Weather", config.Authorize.Routes);
            Assert.Contains("/WeatherForecast", config.Authorize.Routes);

            // 2. JwtSecret
            Assert.NotNull(config.JwtSecret);
            Assert.Equal("bottomhalfx12#@-emstum-api-servic-x#20$46@3211", config.JwtSecret.Key);
            Assert.Equal("bottomhalf-services", config.JwtSecret.Issuer);
            Assert.Equal(72000, config.JwtSecret.ExpiryTimeInSeconds);
            Assert.Equal("emstum_user", config.JwtSecret.Audiance);
            Assert.Equal(2592000, config.JwtSecret.RefreshTokenExpiryTimeInSeconds);

            // 3. ConnectionStrings
            Assert.NotNull(config.ConnectionStrings);
            Assert.True(config.ConnectionStrings.ContainsKey("master"));
            Assert.Contains("ems_master", config.ConnectionStrings["master"]);

            // 4. KafkaServerDetail
            Assert.NotNull(config.KafkaServerDetail);
            Assert.Equal("kafka.bt.svc.cluster.local:9092", config.KafkaServerDetail.BootstrapServers);
            Assert.True(config.KafkaServerDetail.EnableAutoCommit);

            // 5. AdditionalDetails Dictionary<string, string>
            Assert.NotNull(config.AdditionalDetails);
            Assert.True(config.AdditionalDetails.ContainsKey("Credential"));
            Assert.True(config.AdditionalDetails.ContainsKey("Test"));

            // Check that values are string json
            var credentialJson = config.AdditionalDetails["Credential"];
            Assert.Contains("\"Username\":", credentialJson);
            Assert.Contains("admin_service", credentialJson);

            var testJson = config.AdditionalDetails["Test"];
            Assert.StartsWith("[", testJson.Trim());
            Assert.Contains("Item1", testJson);

            // Typed retrieval using GetDetail<T>
            var credentialObj = config.GetDetail<CredentialTestModel>("Credential");
            Assert.NotNull(credentialObj);
            Assert.Equal("admin_service", credentialObj.Username);

            var testList = config.GetDetail<List<TestItemModel>>("Test");
            Assert.NotNull(testList);
            Assert.Equal(2, testList.Count);
            Assert.Equal(101, testList[0].Id);
        }

        [Fact]
        public void OptionalProperties_ShouldNotFailWhenOmitted()
        {
            const string emptyJson = "{}";
            var config = JsonConvert.DeserializeObject<FierHubConfig>(emptyJson);

            Assert.NotNull(config);
            Assert.Null(config.KafkaServerDetail);
            Assert.Null(config.JwtSecret);
            Assert.NotNull(config.Authorize); // Defaults to empty AuthorizeModel
            Assert.NotNull(config.ConnectionStrings); // Defaults to empty dictionary
            Assert.Empty(config.ConnectionStrings);
            Assert.Empty(config.AdditionalDetails);
        }

        [Fact]
        public void FierHubConfig_SingletonThreadSafety()
        {
            FierHubConfig.ResetInstanceForTesting();

            var instances = new FierHubConfig[100];
            Parallel.For(0, 100, i =>
            {
                instances[i] = FierHubConfig.Instance();
            });

            var first = instances[0];
            Assert.NotNull(first);
            for (int i = 1; i < 100; i++)
            {
                Assert.Same(first, instances[i]);
            }
        }

        [Fact]
        public void FierHubRegistry_Builder_CreatesThreadSafeSingleton()
        {
            FierHubRegistry.ResetInstanceForTesting();

            var builder = WebApplication.CreateBuilder();

            var reg1 = FierHubRegistry.Builder(builder);
            var reg2 = FierHubRegistry.Builder(builder);

            Assert.NotNull(reg1);
            Assert.Same(reg1, reg2);
            Assert.Same(reg1, FierHubRegistry.Instance);
        }

        [Fact]
        public async Task ReadConfigurationFileAsync_ShouldDownloadAndPopulateConfigProperly()
        {
            FierHubConfig.ResetInstanceForTesting();
            FierHubRegistry.ResetInstanceForTesting();

            var mockHandler = new MockHttpMessageHandler(req =>
            {
                Assert.Equal("https://www.fierhub.com/api/fileContent/readConfigurationFile", req.RequestUri!.ToString());
                Assert.Equal(HttpMethod.Post, req.Method);

                var responseModel = new ResponseModel
                {
                    statusCode = 200,
                    message = "Success",
                    responseBody = SampleJson
                };

                var httpResponse = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonConvert.SerializeObject(responseModel), System.Text.Encoding.UTF8, "application/json")
                };
                return httpResponse;
            });

            var httpClient = new HttpClient(mockHandler);
            var clientFactory = new MockHttpClientFactory(httpClient);
            var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<FierhubServiceRequest>.Instance;
            var serviceRequest = new FierhubServiceRequest(clientFactory, logger);

            var builder = WebApplication.CreateBuilder();
            builder.Configuration["FierHub:Token"] = "test-token-12345";
            builder.Services.AddSingleton(serviceRequest);

            var registry = FierHubRegistry.Builder(builder);

            // Directly invoke ReadConfigurationFile with only the token
            var config = await registry.ReadConfigurationFileAsync("test-token-12345");

            Assert.NotNull(config);
            Assert.Equal("test-token-12345", config.Token);

            // Verify Authorize
            Assert.NotNull(config.Authorize);
            Assert.Equal(13, config.Authorize.Routes.Count);

            // Verify JwtSecret
            Assert.NotNull(config.JwtSecret);
            Assert.Equal("bottomhalf-services", config.JwtSecret.Issuer);
            Assert.True(config.JwtSecret.IsPrimary);

            // Verify ConnectionStrings
            Assert.NotNull(config.ConnectionStrings);
            Assert.True(config.ConnectionStrings.ContainsKey("master"));

            // Verify KafkaServerDetail
            Assert.NotNull(config.KafkaServerDetail);
            Assert.Equal("kafka.bt.svc.cluster.local:9092", config.KafkaServerDetail.BootstrapServers);
            Assert.True(config.KafkaServerDetail.EnableAutoCommit);

            // Verify Requirement 3: AdditionalDetails contains Credential and Test
            Assert.True(config.AdditionalDetails.ContainsKey("Credential"));
            Assert.True(config.AdditionalDetails.ContainsKey("Test"));
            Assert.Contains("admin_service", config.AdditionalDetails["Credential"]);

            // Verify singleton was updated
            Assert.Same(FierHubConfig.Instance(), config);
        }

        private class MockHttpClientFactory : IHttpClientFactory
        {
            private readonly HttpClient _client;
            public MockHttpClientFactory(HttpClient client) => _client = client;
            public HttpClient CreateClient(string name) => _client;
        }

        private class MockHttpMessageHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;
            public MockHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) => _handler = handler;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                return Task.FromResult(_handler(request));
            }
        }

        private class CredentialTestModel
        {
            public string Username { get; set; }
            public string Password { get; set; }
        }

        private class TestItemModel
        {
            public int Id { get; set; }
            public string Name { get; set; }
        }
    }
}
