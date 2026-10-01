using System.Net.Http.Json;
using System.Security.Cryptography;
using RemoteCmd.Shared;

namespace RemoteCmd.Client;

/// <summary>
/// Trades a one-time pairing code for this machine's own token and saves it to the config file.
/// </summary>
public static class PairCommand
{
    public static async Task<ClientConfig?> RunAsync(string server, string code, string name, bool keepAwake)
    {
        var baseUrl = ClientConfig.NormalizeServer(server);
        var (request, state) = Pairing.Begin(code, name);
        using (state)
        {
            string? seenCert = null;
            using var handler = new SocketsHttpHandler
            {
                // No pin exists yet — pairing is what establishes it. The certificate seen here is
                // only trusted once the relay has proved it knows the code (see Pairing.Finish).
                SslOptions =
                {
                    RemoteCertificateValidationCallback = (_, cert, _, _) =>
                    {
                        seenCert = cert?.GetCertHashString(HashAlgorithmName.SHA256);
                        return true;
                    },
                },
                ConnectTimeout = TimeSpan.FromSeconds(10),
            };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };

            HttpResponseMessage response;
            try { response = await http.PostAsJsonAsync($"{baseUrl}/api/pair", request); }
            catch (HttpRequestException ex)
            {
                Console.Error.WriteLine($"Cannot reach the relay at {baseUrl}: {ex.Message}");
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                Console.Error.WriteLine($"Pairing refused ({(int)response.StatusCode}): {body}");
                return null;
            }

            PairGrant grant;
            try
            {
                var answer = await response.Content.ReadFromJsonAsync<PairResponse>()
                             ?? throw new CryptographicException("Empty answer");
                grant = Pairing.Finish(code, state, answer);
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException or System.Text.Json.JsonException)
            {
                Console.Error.WriteLine($"Pairing failed: {ex.Message}. The code may be wrong, or something between you and the relay is not the relay.");
                return null;
            }

            // The relay states which certificate it serves; the one this connection actually saw
            // must be that one, or someone else answered the TLS handshake.
            if (grant.CertSha256 != null && seenCert != null
                && !string.Equals(grant.CertSha256, seenCert, StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine("Pairing failed: the TLS certificate does not match the one the relay reports.");
                return null;
            }

            return new ClientConfig(baseUrl, grant.Token, grant.Name, keepAwake, grant.CertSha256);
        }
    }
}
