using Fierhub.Service.Library.Model;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;

namespace Fierhub.Service.Library.Middleware
{
    public class RouteValidator(FierHubConfig _fierHubConfig)
    {
        public bool Test(HttpContext context)
        {
            var endpoint = context.GetEndpoint();
            var flag = endpoint?.Metadata?.GetMetadata<AllowAnonymousAttribute>() != null;
            if (flag)
            {
                return true;
            }

            var path = context.Request.Path;

            // 3. Configured whitelist routes (safe prefix matching)
            var configuredRoutes = _fierHubConfig?.Authorize?.Routes;
            if (configuredRoutes != null && configuredRoutes.Count > 0)
            {
                return configuredRoutes.Any(r =>
                    !string.IsNullOrWhiteSpace(r) &&
                    path.StartsWithSegments(r.Trim(), StringComparison.OrdinalIgnoreCase));
            }

            return false;
        }
    }
}
