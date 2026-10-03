namespace Vessel3.Storage;

internal readonly record struct KeyBound(string Key, bool Inclusive)
{
    public static KeyBound After(string key) => new(key, false);
    public static KeyBound From(string key) => new(key, true);
}
