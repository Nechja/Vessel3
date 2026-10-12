using System.Diagnostics.CodeAnalysis;

namespace Vessel3.Storage;

internal interface IChecksumValidator
{
    bool Validate(
        StoredBlob blob,
        DeclaredChecksums declared,
        Stream body,
        out ChecksumSet toStore,
        [NotNullWhen(false)] out Error? error);
}
