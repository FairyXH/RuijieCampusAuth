using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using RuijieCampusAuth.Core.Protocols;

namespace RuijieCampusAuth.Core.Tests;

public sealed class EapTlsStreamTests
{
    [Fact]
    public async Task SslStream_handshake_and_data_over_bridged_streams()
    {
        using var certificate = CreateCertificate();
        using var client = new EapTlsStream();
        using var server = new EapTlsStream();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var pump = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                var fromClient = client.TakeOutgoing(20);
                if (fromClient is not null)
                {
                    server.Feed(fromClient);
                }
                var fromServer = server.TakeOutgoing(20);
                if (fromServer is not null)
                {
                    client.Feed(fromServer);
                }
                await Task.Delay(5, stop.Token);
            }
        });

        using var serverSsl = new SslStream(server, leaveInnerStreamOpen: true);
        using var clientSsl = new SslStream(
            client,
            leaveInnerStreamOpen: true,
            (_, _, _, _) => true);

        var serverTask = serverSsl.AuthenticateAsServerAsync(
            new SslServerAuthenticationOptions
            {
                ServerCertificate = certificate,
                ClientCertificateRequired = false,
                EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12,
            },
            stop.Token);
        var clientTask = clientSsl.AuthenticateAsClientAsync(
            new SslClientAuthenticationOptions
            {
                TargetHost = "localhost",
                EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12,
            },
            stop.Token);
        await Task.WhenAll(serverTask, clientTask);

        var payload = Encoding.ASCII.GetBytes("hello-eap-tls");
        await clientSsl.WriteAsync(payload, stop.Token);
        var received = new byte[payload.Length];
        var read = await serverSsl.ReadAsync(received.AsMemory(), stop.Token);
        Assert.Equal(payload.Length, read);
        Assert.Equal(payload, received);

        stop.Cancel();
        try
        {
            await pump;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static X509Certificate2 CreateCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=localhost",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(
            new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddHours(1));
        return new X509Certificate2(
            certificate.Export(X509ContentType.Pfx),
            (string?)null,
            X509KeyStorageFlags.Exportable);
    }
}
