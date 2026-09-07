using System.Security.Claims;
using System.Text;
using Delivery.Api.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Delivery.Api.Statements;

/// <summary>
/// Mints a bearer token for local development.
/// </summary>
/// <remarks>
/// <para>
/// DEVELOPMENT ONLY, AND GUARDED TWICE. It is mapped only when the environment is Development, and
/// it can only sign a token if <c>Jwt:DevelopmentSigningKey</c> is configured - which
/// <see cref="JwtOptionsValidator"/> refuses to allow outside Development, failing startup rather
/// than starting a service that can mint its own credentials.
/// </para>
/// <para>
/// It exists because the read path is behind bearer authentication, so without it the acceptance
/// checks cannot be run and a reviewer cannot exercise the API at all. That is a real need; the
/// answer is to make the affordance loud and narrow rather than to leave the endpoints
/// unauthenticated for convenience.
/// </para>
/// <para>
/// SecurityTests asserts the mapping is inside the Development guard.
/// </para>
/// </remarks>
public static class DevTokenEndpoint
{
    /// <summary>Maps the development token endpoint.</summary>
    /// <param name="app">The web application.</param>
    /// <returns>The application, for chaining.</returns>
    public static WebApplication MapDevTokenEndpoint(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // GET and POST alike: a reviewer can paste the URL into a browser and read the token off
        // the page, which is the lowest-friction way to get past the first 401. Development only.
        app.MapMethods("/v1/dev/tokens", ["GET", "POST"], (string customerId, IOptions<JwtOptions> options, bool staff = false, bool dpo = false) =>
        {
            JwtOptions jwt = options.Value;

            if (string.IsNullOrWhiteSpace(jwt.DevelopmentSigningKey))
            {
                return Results.Problem(
                    detail: "Jwt:DevelopmentSigningKey is not configured, so no token can be signed.",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            if (!Guid.TryParse(customerId, out Guid subject))
            {
                return Results.Problem(
                    detail: "customerId must be a UUID.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            var credentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.DevelopmentSigningKey)),
                SecurityAlgorithms.HmacSha256);

            var descriptor = new SecurityTokenDescriptor
            {
                Issuer = jwt.Issuer,
                Audience = jwt.Audience,

                // `sub` IS the customer identifier. The route parameter on every statement endpoint
                // is untrusted input compared against this.
                // `sub` plus, on request, the operator scope. Opt-in and off by default, so an
                // ordinary development token cannot read the audit trail by accident.
                Subject = new ClaimsIdentity(staff
                    ? [
                        new Claim("sub", subject.ToString("D")),

                        // dpo stacks ON staff (erasure sits above the operator scope); the demo
                        // and DPO-scope tests are the only consumers, and only in Development.
                        new Claim("scope", dpo
                            ? DeliveryApiExtensions.StaffScope + " " + DeliveryApiExtensions.DpoScope
                            : DeliveryApiExtensions.StaffScope),
                      ]
                    : [new Claim("sub", subject.ToString("D"))]),

                // Deliberately short. A development token that lasts a week ends up pasted into a
                // script, and the script ends up somewhere else.
                Expires = DateTime.UtcNow.AddHours(1),
                SigningCredentials = credentials,
            };

            string token = new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = true }
                .CreateToken(descriptor);

            return Results.Ok(new
            {
                accessToken = token,
                tokenType = "Bearer",
                expiresInSeconds = 3600,
                subject = subject.ToString("D"),
                scope = staff
                    ? (dpo
                        ? DeliveryApiExtensions.StaffScope + " " + DeliveryApiExtensions.DpoScope
                        : DeliveryApiExtensions.StaffScope)
                    : null,
            });
        })
        .AllowAnonymous()
        .ExcludeFromDescription()
        .WithName("IssueDevelopmentToken");

        return app;
    }
}
