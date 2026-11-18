namespace AutoExtract.Core;

public sealed class ArchiveIntegrityException(string message) : IOException(message);
