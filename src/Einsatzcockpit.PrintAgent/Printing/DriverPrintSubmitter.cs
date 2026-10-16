using System.Drawing;
using System.Drawing.Printing;
using System.Text.Json;
using Einsatzcockpit.PrintAgent.Core.Abstractions;
using Einsatzcockpit.PrintAgent.Core.Printing;
using Einsatzcockpit.PrintAgent.Core.Protocol;
using PDFtoImage;

namespace Einsatzcockpit.PrintAgent.Printing;

/// <summary>
/// Druckweg "driver": PDF-Seiten mit PDFium rastern und über den Windows-Druckertreiber
/// ausgeben. Funktioniert mit jedem installierten Drucker; Kopien, Duplex, Papierformat,
/// Fach und Farbe gehen über die DEVMODE-Einstellungen des Treibers.
/// </summary>
public sealed class DriverPrintSubmitter(WindowsSpooler spooler) : IPrintSubmitter
{
    public string Name => "driver";

    public async Task<SubmitResult> SubmitAsync(PrintRequest request, CancellationToken cancellationToken)
    {
        var settings = new PrinterSettings
        {
            PrinterName = request.Route.LocalQueue,
            Copies = (short)Math.Clamp(request.Options?.Copies ?? 1, 1, 999),
            Collate = OptionBool(request.Route, "collate"),
        };
        if (!settings.IsValid)
        {
            throw new PrintRejectedException($"Windows-Drucker \"{request.Route.LocalQueue}\" ist nicht verfügbar.");
        }
        Configure(settings, request);

        byte[] pdf;
        IReadOnlyList<SizeF> seiten;
        try
        {
            pdf = await File.ReadAllBytesAsync(request.PdfPath, cancellationToken);
            seiten = Conversion.GetPageSizes(pdf, null).ToList();
        }
        catch (Exception ex)
        {
            throw new PrintRejectedException("PDF konnte nicht gelesen werden.", ex);
        }
        if (seiten.Count == 0)
        {
            throw new PrintRejectedException("PDF enthält keine Seiten.");
        }

        var dpi = Dpi(request.Route);
        var einpassen = Scaling(request.Route);
        var seite = 0;
        var gedruckteSeiten = 0;
        Exception? seitenFehler = null;

        using var document = new PrintDocument
        {
            PrinterSettings = settings,
            DocumentName = request.DocumentName,
            PrintController = new StandardPrintController(),
        };
        // Ausrichtung muss VOR PrintPage je Seite gesetzt werden (QueryPageSettings).
        document.QueryPageSettings += (_, e) =>
        {
            if (seite < seiten.Count)
            {
                var groesse = seiten[seite];
                e.PageSettings.Landscape = Orientation(request.Route, groesse.Width > groesse.Height);
            }
        };
        document.PrintPage += (_, e) =>
        {
            try
            {
                using var bild = Render(pdf, seite, dpi);
                Draw(e.Graphics!, e.MarginBounds, bild, einpassen);
                gedruckteSeiten++;
            }
            catch (Exception ex)
            {
                seitenFehler = ex;
                e.HasMorePages = false;
                return;
            }
            seite++;
            e.HasMorePages = seite < seiten.Count;
        };

        try
        {
            document.Print();
        }
        catch (Exception ex) when (gedruckteSeiten == 0)
        {
            // Treiber/Spooler hat den Auftrag abgelehnt, bevor eine Seite erzeugt wurde.
            throw new PrintRejectedException("Druckauftrag konnte nicht gestartet werden: " + ex.Message, ex);
        }
        if (seitenFehler is not null)
        {
            if (gedruckteSeiten == 0)
            {
                throw new PrintRejectedException("Erste Seite konnte nicht gerastert werden.", seitenFehler);
            }
            // Ein Teil ist schon beim Spooler: Ausgang unklar (JobProcessor meldet uncertain).
            throw new IOException($"Seite {seite + 1} konnte nicht gerastert werden.", seitenFehler);
        }

        for (var versuch = 0; versuch < 20; versuch++)
        {
            var job = await spooler.FindJobByDocumentNameAsync(request.Route.LocalQueue, request.DocumentName, cancellationToken);
            if (job is not null)
            {
                return new SubmitResult(job.SpoolRef);
            }
            await Task.Delay(500, cancellationToken);
        }
        // Sehr schneller Drucker: Auftrag war schon wieder aus der Warteschlange.
        return new SubmitResult(WindowsSpooler.GoneRef(request.DocumentName));
    }

    private static Image Render(byte[] pdf, int seite, int dpi)
    {
        using var png = new MemoryStream();
        Conversion.SavePng(png, pdf, new Index(seite), null, new RenderOptions(Dpi: dpi));
        png.Position = 0;
        using var quelle = Image.FromStream(png);
        return new Bitmap(quelle);
    }

    private static void Configure(PrinterSettings settings, PrintRequest request)
    {
        settings.Duplex = PrintValueMappings.Duplex(request.Options?.Duplex) switch
        {
            "long-edge" => Duplex.Vertical,
            "short-edge" => Duplex.Horizontal,
            _ => Duplex.Simplex,
        };

        var media = PrintValueMappings.Media(request.Options?.Media);
        var kind = media == "A3" ? PaperKind.A3 : PaperKind.A4;
        var papier = settings.PaperSizes.Cast<PaperSize>()
            .FirstOrDefault(p => p.Kind == kind || p.PaperName.Equals(media, StringComparison.OrdinalIgnoreCase));
        if (papier is not null)
        {
            settings.DefaultPageSettings.PaperSize = papier;
        }

        var fach = OptionString(request.Route, "tray");
        if (!string.IsNullOrWhiteSpace(fach))
        {
            var quelle = settings.PaperSources.Cast<PaperSource>()
                .FirstOrDefault(s => s.SourceName.Equals(fach, StringComparison.OrdinalIgnoreCase));
            if (quelle is not null)
            {
                settings.DefaultPageSettings.PaperSource = quelle;
            }
        }

        var farbe = PrintValueMappings.IsColor(request.Options?.Color);
        var erzwungen = OptionString(request.Route, "force_color");
        if (string.Equals(erzwungen, "monochrome", StringComparison.OrdinalIgnoreCase))
        {
            farbe = false;
        }
        else if (string.Equals(erzwungen, "color", StringComparison.OrdinalIgnoreCase))
        {
            farbe = true;
        }
        settings.DefaultPageSettings.Color = farbe && settings.SupportsColor;
    }

    private static void Draw(Graphics graphics, Rectangle bereich, Image bild, bool einpassen)
    {
        var faktor = einpassen
            ? Math.Min((float)bereich.Width / bild.Width, (float)bereich.Height / bild.Height)
            : 100f / bild.HorizontalResolution; // Originalgröße: Pixel → 1/100 Zoll
        var breite = (int)(bild.Width * faktor);
        var hoehe = (int)(bild.Height * faktor);
        graphics.DrawImage(bild, bereich.X + (bereich.Width - breite) / 2, bereich.Y + (bereich.Height - hoehe) / 2, breite, hoehe);
    }

    private static JsonElement Option(RouteConfig route, string name) => CommandSubmitter.Option(route, name);

    private static string? OptionString(RouteConfig route, string name)
    {
        var wert = Option(route, name);
        return wert.ValueKind == JsonValueKind.String ? wert.GetString() : null;
    }

    private static bool OptionBool(RouteConfig route, string name) => Option(route, name).ValueKind == JsonValueKind.True;

    private static int Dpi(RouteConfig route)
    {
        var wert = Option(route, "dpi");
        return wert.ValueKind == JsonValueKind.Number && wert.TryGetInt32(out var dpi) ? Math.Clamp(dpi, 72, 1200) : 300;
    }

    private static bool Scaling(RouteConfig route) =>
        !string.Equals(OptionString(route, "scaling"), "actual", StringComparison.OrdinalIgnoreCase);

    private static bool Orientation(RouteConfig route, bool quer) => OptionString(route, "orientation")?.ToLowerInvariant() switch
    {
        "landscape" or "quer" => true,
        "portrait" or "hoch" => false,
        _ => quer,
    };
}
