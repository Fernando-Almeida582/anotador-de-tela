using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace Anotador;

internal static class LensSearch
{
    internal static Task OpenAsync(BitmapSource image) => ServeAsync(image, address =>
    {
        Process.Start(new ProcessStartInfo(address.AbsoluteUri) { UseShellExecute = true })?.Dispose();
    });

    internal static async Task ServeAsync(BitmapSource image, Action<Uri> openBrowser)
    {
        byte[] page = BuildPage(image);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        string path = "/" + Guid.NewGuid().ToString("N");
        openBrowser(new Uri($"http://127.0.0.1:{port}{path}"));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        while (true)
        {
            using var browser = await listener.AcceptTcpClientAsync(deadline.Token).ConfigureAwait(false);
            using var stream = browser.GetStream();
            using var requestDeadline = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            requestDeadline.CancelAfter(TimeSpan.FromSeconds(3));
            try
            {
                string? requestLine = await ReadRequestLine(stream, requestDeadline.Token).ConfigureAwait(false);
                if (requestLine != $"GET {path} HTTP/1.1" && requestLine != $"GET {path} HTTP/1.0")
                {
                    await stream.WriteAsync("HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(),
                        requestDeadline.Token).ConfigureAwait(false);
                    continue;
                }

                string headers = $"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {page.Length}\r\n"
                    + "Cache-Control: no-store\r\nReferrer-Policy: no-referrer\r\nX-Content-Type-Options: nosniff\r\n"
                    + "Content-Security-Policy: default-src 'none'; script-src 'unsafe-inline'; form-action https://lens.google.com https://*.google.com; frame-ancestors 'none'; base-uri 'none'\r\n"
                    + "Connection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(headers), deadline.Token).ConfigureAwait(false);
                await stream.WriteAsync(page, deadline.Token).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (!deadline.IsCancellationRequested) { }
            catch (IOException) when (!deadline.IsCancellationRequested) { }
        }
    }

    private static async Task<string?> ReadRequestLine(NetworkStream stream, CancellationToken token)
    {
        byte[] headers = new byte[16 * 1024];
        int total = 0;
        while (total < headers.Length)
        {
            int read = await stream.ReadAsync(headers.AsMemory(total), token).ConfigureAwait(false);
            if (read == 0) return null;
            int scanStart = Math.Max(0, total - 3);
            total += read;
            if (headers.AsSpan(scanStart, total - scanStart).IndexOf("\r\n\r\n"u8) >= 0)
            {
                int end = headers.AsSpan(0, total).IndexOf("\r\n"u8);
                return Encoding.ASCII.GetString(headers, 0, end);
            }
        }
        return null;
    }

    private static byte[] BuildPage(BitmapSource image)
    {
        using var png = new MemoryStream();
        Screenshot.WritePng(image, png);
        string base64 = Convert.ToBase64String(png.GetBuffer(), 0, checked((int)png.Length));
        string language = Uri.EscapeDataString(CultureInfo.CurrentUICulture.Name);
        string action = FormattableString.Invariant(
            $"https://lens.google.com/v3/upload?ep=cntpubb&hl={language}&re=df&s=4&vpw={image.PixelWidth}&vph={image.PixelHeight}");
        return Encoding.UTF8.GetBytes($$"""
            <!doctype html>
            <html lang="pt-BR">
            <meta charset="utf-8">
            <title>Pesquisa com Google Lens</title>
            <p id="status">Enviando o recorte ao Google Lens…</p>
            <noscript>Ative o JavaScript do navegador e faça uma nova seleção no Anotador.</noscript>
            <form id="lens" method="post" enctype="multipart/form-data" action="{{WebUtility.HtmlEncode(action)}}">
                <input id="image" name="encoded_image" type="file" hidden>
            </form>
            <script>
                try {
                    const bytes = Uint8Array.from(atob('{{base64}}'), character => character.charCodeAt(0));
                    const data = new DataTransfer();
                    data.items.add(new File([bytes], 'recorte.png', { type: 'image/png' }));
                    document.getElementById('image').files = data.files;
                    const form = document.getElementById('lens');
                    const destination = new URL(form.action);
                    destination.searchParams.set('st', Date.now().toString());
                    form.action = destination.href;
                    form.submit();
                } catch {
                    document.getElementById('status').textContent =
                        'O navegador não conseguiu enviar o recorte. Faça uma nova seleção no Anotador.';
                }
            </script>
            </html>
            """);
    }
}

