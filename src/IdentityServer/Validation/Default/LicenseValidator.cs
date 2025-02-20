// Copyright (c) Duende Software. All rights reserved.
// See LICENSE in the project root for license information.


using Duende.IdentityServer.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.IdentityModel.JsonWebTokens;

namespace Duende.IdentityServer.Validation;

internal class LicenseValidator
{
    static readonly string[] LicenseFileNames = new[] 
    {
        "Duende_License.key",
        "Duende_IdentityServer_License.key",
    };

    static ILogger _logger;
    static IdentityServerOptions _options;
    static License _license;

    static ConcurrentDictionary<string, byte> _clientIds = new ConcurrentDictionary<string, byte>();
    static ConcurrentDictionary<string, byte> _issuers = new ConcurrentDictionary<string, byte>();

    public static void Initalize(ILoggerFactory loggerFactory, IdentityServerOptions options)
    {
        _logger = loggerFactory.CreateLogger("Duende");
        _options = options;

        var key = options.LicenseKey ?? LoadFromFile();
        _license = ValidateKey(key);
    }

    private static string LoadFromFile()
    {
        foreach (var name in LicenseFileNames)
        {
            var path = Path.Combine(Directory.GetCurrentDirectory(), name);
            if (File.Exists(path))
            {
                return File.ReadAllText(path).Trim();
            }
        }

        return null;
    }

    public static void ValidateLicense()
    {
        var errors = new List<string>();

        if (_license == null)
        {
            var message = "You do not have a valid license key for the Duende software. " +
                          "This is allowed for development and testing scenarios. " +
                          "If you are running in production you are required to have a licensed version. Please start a conversation with us: https://duendesoftware.com/contact";

            _logger.LogWarning(message);
            return;
        }
        else if (_license.IsBffEdition)
        {
            errors.Add($"Your Duende software license is not valid for IdentityServer.");
        }
        else
        {
            Action<string, object[]> func = _license.ISVFeature ? _logger.LogTrace : _logger.LogDebug;
            func.Invoke("The validated licence key details: {@license}", new[] { _license });

            if (_license.Expiration.HasValue)
            {
                var diff = DateTime.UtcNow.Date.Subtract(_license.Expiration.Value.Date).TotalDays;
                if (diff > 0 && !_license.ISVFeature)
                {
                    errors.Add($"Your license for the Duende software expired {diff} days ago.");
                }
            }

            if (_options.KeyManagement.Enabled && !_license.KeyManagementFeature)
            {
                errors.Add(
                    "You have automatic key management enabled, but you do not have a valid license for that feature of Duende IdentityServer.");
            }
        }

        if (errors.Count > 0)
        {
            foreach (var err in errors)
            {
                _logger.LogError(err);
            }

            if (_license != null)
            {
                _logger.LogError(
                    "Please contact {licenceContact} from {licenseCompany} to obtain a valid license for the Duende software.",
                    _license.ContactInfo, _license.CompanyName);
            }
        }
        else
        {
            if (_license.Expiration.HasValue)
            {
                Action<string, object[]> log = _license.ISVFeature ? _logger.LogTrace : _logger.LogInformation;
                log.Invoke("You have a valid license key for the Duende software {edition} edition for use at {licenseCompany}. The license expires on {licenseExpiration}.",
                    new object[] { _license.Edition, _license.CompanyName, _license.Expiration.Value.ToLongDateString() });
            }
            else
            {
                Action<string, object[]> log = _license.ISVFeature ? _logger.LogTrace : _logger.LogInformation;
                log.Invoke(
                    "You have a valid license key for the Duende software {edition} edition for use at {licenseCompany}.",
                    new object[] { _license.Edition, _license.CompanyName });
            }
        }
    }

    public static void ValidateClient(string clientId)
    {
        if (_license != null)
        {
            if (_license.ClientLimit.HasValue)
            {
                _clientIds.TryAdd(clientId, 1);
                if (_clientIds.Count > _license.ClientLimit)
                {
                    _logger.LogError(
                        "Your license for Duende IdentityServer only permits {clientLimit} number of clients. You have processed requests for {clientCount}. The clients used were: {clients}.",
                        _license.ClientLimit, _clientIds.Count, _clientIds.Keys.ToArray());
                }
            }
        }
    }

    public static void ValidateIssuer(string iss)
    {
        if (_license != null)
        {
            if (_license.IssuerLimit.HasValue)
            {
                _issuers.TryAdd(iss, 1);
                if (_issuers.Count > _license.IssuerLimit)
                {
                    _logger.LogError(
                        "Your license for Duende IdentityServer only permits {issuerLimit} number of issuers. You have processed requests for {issuerCount}. The issuers used were: {issuers}.",
                        _license.IssuerLimit, _issuers.Count, _issuers.Keys.ToArray());
                }
            }
        }
    }

    public static void ValidateResourceIndicators(string resourceIndicator)
    {
        if (_license != null && !String.IsNullOrWhiteSpace(resourceIndicator) && !_license.ResourceIsolationFeature)
        {
            _logger.LogError(
                "A request was made using a resource indicator. Your license for Duende IdentityServer does not permit resource isolation.");
        }
    }

    public static void ValidateResourceIndicators(IEnumerable<string> resourceIndicators)
    {
        if (_license != null && resourceIndicators?.Any() == true && !_license.ResourceIsolationFeature)
        {
            _logger.LogError(
                "A request was made using a resource indicator. Your license for Duende IdentityServer does not permit resource isolation.");
        }
    }

    public static void ValidateDynamicProviders()
    {
        if (_license != null && !_license.DynamicProvidersFeature)
        {
            _logger.LogError("A request was made invoking a dynamic provider. Your license for Duende IdentityServer does not permit dynamic providers.");
        }
    }

    public static void ValidateCiba()
    {
        if (_license != null && !_license.CibaFeature)
        {
            _logger.LogError("A CIBA (client initiated backchannel authentication) request was made. Your license for Duende IdentityServer does not permit the CIBA feature.");
        }
    }

    internal static License ValidateKey(string licenseKey)
    {
        if (!String.IsNullOrWhiteSpace(licenseKey))
        {
            var handler = new JsonWebTokenHandler();


            var rsa = new RSAParameters
            {
                Exponent = Convert.FromBase64String("AQAB"),
                Modulus = Convert.FromBase64String(
                    "tAHAfvtmGBng322TqUXF/Aar7726jFELj73lywuCvpGsh3JTpImuoSYsJxy5GZCRF7ppIIbsJBmWwSiesYfxWxBsfnpOmAHU3OTMDt383mf0USdqq/F0yFxBL9IQuDdvhlPfFcTrWEL0U2JsAzUjt9AfsPHNQbiEkOXlIwtNkqMP2naynW8y4WbaGG1n2NohyN6nfNb42KoNSR83nlbBJSwcc3heE3muTt3ZvbpguanyfFXeoP6yyqatnymWp/C0aQBEI5kDahOU641aDiSagG7zX1WaF9+hwfWCbkMDKYxeSWUkQOUOdfUQ89CQS5wrLpcU0D0xf7/SrRdY2TRHvQ=="),
            };

            var key = new RsaSecurityKey(rsa)
            {
                KeyId = "IdentityServerLicensekey/7ceadbb78130469e8806891025414f16"
            };

            var parms = new TokenValidationParameters
            {
                ValidIssuer = "https://duendesoftware.com",
                ValidAudience = "IdentityServer",
                IssuerSigningKey = key,
                ValidateLifetime = false
            };

            var validateResult = handler.ValidateToken(licenseKey, parms);
            if (validateResult.IsValid)
            {
                return new License(new ClaimsPrincipal(validateResult.ClaimsIdentity));
            }
            else
            {
                _logger.LogCritical(validateResult.Exception, "Error validating the Duende software license key");
            }
        }

        return null;
    }
}