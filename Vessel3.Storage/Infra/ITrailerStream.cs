namespace Vessel3.Storage;

internal interface ITrailerStream
{
    IReadOnlyDictionary<string, string> Trailers { get; }
}
