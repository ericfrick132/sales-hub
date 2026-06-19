//  dentro de InstagramClient.cs en SalesHub.Infrastructure/Instagram/
//  pegar este método antes del método DisposeAsync que ya existe

/// <summary>
/// Postea una imagen en Instagram desde una URL pública.
/// Descarga la imagen, la sube como post con la caption indicada.
/// </summary>
public async Task<bool> PostImageAsync(string imageUrl, string caption, CancellationToken ct = default)
{
    EnsureLoggedIn();

    _log.LogInformation("Posteando imagen en Instagram...");

    try
    {
        await _page!.GotoAsync("https://www.instagram.com/", new PageGotoOptions
        {
            WaitUntil = WaitUntilState.DOMContentLoaded,
            Timeout = _opts.NavigationTimeoutMs
        });

        await RandomDelayAsync();

        var createBtn = await _page.QuerySelectorAsync(
            "svg[aria-label='New post'], " +
            "a[href='/create/style/'], " +
            "div[role='button']:has-text('Create')");

        if (createBtn is null)
        {
            await _page.GotoAsync("https://www.instagram.com/create/style/", new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = _opts.NavigationTimeoutMs
            });
            await Task.Delay(3000, ct);
        }
        else
        {
            await createBtn.ClickAsync();
            await Task.Delay(2000, ct);
        }

        var tempPath = Path.Combine(Path.GetTempPath(), $"ig_post_{Guid.NewGuid()}.jpg");
        try
        {
            using var http = new HttpClient();
            var imageBytes = await http.GetByteArrayAsync(imageUrl, ct);
            await File.WriteAllBytesAsync(tempPath, imageBytes, ct);

            var fileInput = await _page.QuerySelectorAsync("input[type='file']");
            if (fileInput is null)
            {
                _log.LogWarning("No se encontró el input de archivo para subir imagen");
                return false;
            }

            await fileInput.SetInputFilesAsync(tempPath);
            await Task.Delay(4000, ct);

            for (var step = 0; step < 3; step++)
            {
                var nextBtn = await _page.QuerySelectorAsync(
                    "button:has-text('Next'), " +
                    "div[role='button']:has-text('Next'), " +
                    "button:has-text('Siguiente'), " +
                    "div[role='button']:has-text('Siguiente')");

                if (nextBtn is null) break;
                await nextBtn.ClickAsync();
                await Task.Delay(2500, ct);
            }

            var captionInput = await _page.QuerySelectorAsync(
                "div[aria-label='Write a caption...'], " +
                "div[aria-label='Escribe un pie de foto...'], " +
                "textarea[aria-label='Write a caption...']");

            if (captionInput is not null)
            {
                await captionInput.ClickAsync();
                await captionInput.FillAsync(caption);
                await RandomDelayAsync();
            }

            var shareBtn = await _page.QuerySelectorAsync(
                "button:has-text('Share'), " +
                "div[role='button']:has-text('Share'), " +
                "button:has-text('Compartir'), " +
                "div[role='button']:has-text('Compartir')");

            if (shareBtn is null)
            {
                _log.LogWarning("No se encontró botón Share");
                return false;
            }

            await shareBtn.ClickAsync();
            await Task.Delay(5000, ct);

            var posted = _page.Url.Contains("instagram.com") &&
                         !_page.Url.Contains("create");

            _log.LogInformation("Post {Result}", posted ? "exitoso" : "falló");
            return posted;
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }
    catch (Exception ex)
    {
        _log.LogError(ex, "Error al postear imagen");
        return false;
    }
}
