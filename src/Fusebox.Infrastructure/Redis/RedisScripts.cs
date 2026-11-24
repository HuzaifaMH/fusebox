using System.Reflection;
using StackExchange.Redis;

namespace Fusebox.Infrastructure.Redis;

/// <summary>The Lua scripts Redis runs atomically, loaded once from embedded resources.</summary>
internal static class RedisScripts
{
    public static LuaScript Reserve { get; } = Load("reserve.lua");

    public static LuaScript Settle { get; } = Load("settle.lua");

    private static LuaScript Load(string fileName)
    {
        var assembly = typeof(RedisScripts).Assembly;
        var resourceName = $"{typeof(RedisScripts).Namespace}.Scripts.{fileName}";

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded Lua script '{resourceName}' was not found.");
        using var reader = new StreamReader(stream);

        return LuaScript.Prepare(reader.ReadToEnd());
    }
}
