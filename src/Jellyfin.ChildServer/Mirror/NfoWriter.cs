using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using MediaBrowser.Model.Dto;

namespace Jellyfin.ChildServer.Mirror;

/// <summary>
/// Writes the NFO sidecar files that let the local metadata reader describe mirrored items
/// without contacting the internet.
/// </summary>
public static class NfoWriter
{
    private static readonly XmlWriterSettings _settings = new()
    {
        Indent = true,
        Encoding = new UTF8Encoding(false)
    };

    /// <summary>
    /// Writes a <c>movie.nfo</c> document.
    /// </summary>
    /// <param name="item">The parent movie.</param>
    /// <returns>The XML text.</returns>
    public static string Movie(BaseItemDto item) => Write("movie", item, writer => WriteCommonFields(writer, item));

    /// <summary>
    /// Writes a <c>tvshow.nfo</c> document.
    /// </summary>
    /// <param name="item">The parent series.</param>
    /// <returns>The XML text.</returns>
    public static string Series(BaseItemDto item) => Write("tvshow", item, writer => WriteCommonFields(writer, item));

    /// <summary>
    /// Writes a <c>season.nfo</c> document.
    /// </summary>
    /// <param name="item">The parent season.</param>
    /// <returns>The XML text.</returns>
    public static string Season(BaseItemDto item) => Write("season", item, writer =>
    {
        if (item.IndexNumber.HasValue)
        {
            writer.WriteElementString("seasonnumber", item.IndexNumber.Value.ToString(CultureInfo.InvariantCulture));
        }

        WriteCommonFields(writer, item);
    });

    /// <summary>
    /// Writes an episode <c>.nfo</c> document.
    /// </summary>
    /// <param name="item">The parent episode.</param>
    /// <returns>The XML text.</returns>
    public static string Episode(BaseItemDto item) => Write("episodedetails", item, writer =>
    {
        WriteCommonFields(writer, item);
        WriteOptional(writer, "showtitle", item.SeriesName);
        if (item.ParentIndexNumber.HasValue)
        {
            writer.WriteElementString("season", item.ParentIndexNumber.Value.ToString(CultureInfo.InvariantCulture));
        }

        if (item.IndexNumber.HasValue)
        {
            writer.WriteElementString("episode", item.IndexNumber.Value.ToString(CultureInfo.InvariantCulture));
        }

        if (item.IndexNumberEnd.HasValue)
        {
            writer.WriteElementString("episodenumberend", item.IndexNumberEnd.Value.ToString(CultureInfo.InvariantCulture));
        }

        if (item.PremiereDate.HasValue)
        {
            writer.WriteElementString("aired", item.PremiereDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        }
    });

    private static string Write(string rootElement, BaseItemDto item, Action<XmlWriter> body)
    {
        ArgumentNullException.ThrowIfNull(item);

        using var stream = new MemoryStream();
        using (var writer = XmlWriter.Create(stream, _settings))
        {
            writer.WriteStartDocument(true);
            writer.WriteStartElement(rootElement);
            body(writer);
            writer.WriteEndElement();
            writer.WriteEndDocument();
        }

        return Encoding.UTF8.GetString(stream.ToArray()) + Environment.NewLine;
    }

    private static void WriteCommonFields(XmlWriter writer, BaseItemDto item)
    {
        WriteOptional(writer, "title", item.Name);
        WriteOptional(writer, "originaltitle", item.OriginalTitle);
        WriteOptional(writer, "sorttitle", item.SortName);
        if (item.ProductionYear is > 0)
        {
            writer.WriteElementString("year", item.ProductionYear.Value.ToString(CultureInfo.InvariantCulture));
        }

        if (item.PremiereDate.HasValue && !string.Equals(item.Type.ToString(), "Episode", StringComparison.Ordinal))
        {
            writer.WriteElementString("premiered", item.PremiereDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        }

        WriteOptional(writer, "plot", item.Overview);
        WriteOptional(writer, "tagline", item.Taglines?.FirstOrDefault());
        WriteOptional(writer, "mpaa", item.OfficialRating);
        if (item.CommunityRating.HasValue)
        {
            writer.WriteElementString("rating", item.CommunityRating.Value.ToString("0.0", CultureInfo.InvariantCulture));
        }

        foreach (var genre in item.Genres ?? Array.Empty<string>())
        {
            WriteOptional(writer, "genre", genre);
        }

        foreach (var studio in item.Studios ?? Array.Empty<NameGuidPair>())
        {
            WriteOptional(writer, "studio", studio.Name);
        }

        foreach (var tag in item.Tags ?? Array.Empty<string>())
        {
            WriteOptional(writer, "tag", tag);
        }

        var first = true;
        foreach (var providerId in item.ProviderIds ?? new System.Collections.Generic.Dictionary<string, string>())
        {
            if (string.IsNullOrWhiteSpace(providerId.Key) || string.IsNullOrWhiteSpace(providerId.Value))
            {
                continue;
            }

            writer.WriteStartElement("uniqueid");
            writer.WriteAttributeString("type", providerId.Key);
            writer.WriteAttributeString("default", first ? "true" : "false");
            writer.WriteString(providerId.Value);
            writer.WriteEndElement();
            first = false;
        }
    }

    private static void WriteOptional(XmlWriter writer, string element, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            writer.WriteElementString(element, value);
        }
    }
}
