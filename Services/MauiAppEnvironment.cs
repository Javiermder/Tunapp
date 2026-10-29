namespace Tunapp;

internal sealed class MauiAppEnvironment : IAppEnvironment
{
    public string DataDirectory => FileSystem.AppDataDirectory;
    public bool IsConnected => Connectivity.Current.NetworkAccess == NetworkAccess.Internet;
    public Task<string?> ReadSessionAsync(string key) => SecureStorage.Default.GetAsync(key);
    public Task WriteSessionAsync(string key, string? value)
    {
        if (value is not null) return SecureStorage.Default.SetAsync(key, value);
        SecureStorage.Default.Remove(key); return Task.CompletedTask;
    }
}
