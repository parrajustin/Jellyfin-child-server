using System;

namespace Jellyfin.ChildServer.Mirror;

/// <summary>
/// An image downloaded from the parent server.
/// </summary>
/// <param name="Bytes">The image bytes.</param>
/// <param name="ContentType">The content type, when the parent sent one.</param>
public sealed record ParentImage(ReadOnlyMemory<byte> Bytes, string? ContentType);
