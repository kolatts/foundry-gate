using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using Scalar.AspNetCore;

namespace FoundryGate.Api.Extensions;

/// <summary>
/// OpenAPI document generation (.NET 10's built-in <c>Microsoft.AspNetCore.OpenApi</c>, per
/// imagile-app's precedent — not Swashbuckle) plus a bearer security scheme (issue #27: "so
/// the Swagger UI can send Entra tokens") and a dev-only Scalar UI, since the built-in
/// generator serves only the raw JSON document with no UI of its own.
/// </summary>
public static class SwaggerExtensions
{
    /// <summary>Registers OpenAPI generation with the bearer security scheme document transformer.</summary>
    public static IServiceCollection AddFoundryGateOpenApi(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOpenApi(options => options.AddDocumentTransformer<BearerSecuritySchemeTransformer>());

        return services;
    }

    /// <summary>Maps the OpenAPI JSON document and a Scalar UI. Callers gate this to
    /// <c>AppEnvironment.Types.local</c> — it's dev-only.</summary>
    public static WebApplication MapFoundryGateOpenApi(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapOpenApi();
        app.MapScalarApiReference();

        return app;
    }
}

/// <summary>
/// Adds a <c>Bearer</c> security scheme to the generated OpenAPI document and requires it on
/// every operation, so the Scalar UI's "Authorize" flow can attach an Entra ID access token.
/// Standard sample pattern for <c>Microsoft.AspNetCore.OpenApi</c> (docs: "Customize the
/// OpenAPI document with a JWT bearer scheme"), adapted to the Microsoft.OpenApi 2.x object
/// model (security requirements key on <see cref="OpenApiSecuritySchemeReference"/>, not an
/// inline scheme with a <c>Reference</c> property).
/// </summary>
internal sealed class BearerSecuritySchemeTransformer(IAuthenticationSchemeProvider authenticationSchemeProvider)
    : IOpenApiDocumentTransformer
{
    private const string SchemeId = "Bearer";

    public async Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);

        var authenticationSchemes = await authenticationSchemeProvider.GetAllSchemesAsync();
        if (!authenticationSchemes.Any(scheme => scheme.Name == JwtBearerDefaults.AuthenticationScheme))
        {
            return;
        }

        var securityScheme = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            In = ParameterLocation.Header,
            Description = "Entra ID bearer token.",
        };

        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes[SchemeId] = securityScheme;

        var schemeReference = new OpenApiSecuritySchemeReference(SchemeId, document, externalResource: null);
        var securityRequirement = new OpenApiSecurityRequirement
        {
            [schemeReference] = [],
        };

        foreach (var pathItem in document.Paths.Values)
        {
            if (pathItem?.Operations is null)
            {
                continue;
            }

            foreach (var operation in pathItem.Operations.Values)
            {
                if (operation is null)
                {
                    continue;
                }

                operation.Security ??= [];
                operation.Security.Add(securityRequirement);
            }
        }
    }
}
