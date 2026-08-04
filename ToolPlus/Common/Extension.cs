using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Primitives;

namespace ToolPlus.Common
{
    public static class Extension
    {
        public static bool IsEmpty(this string? value) => string.IsNullOrWhiteSpace(value);

        public static bool IsEmpty(this StringValues value) => StringValues.IsNullOrEmpty(value);

        public static Guid GetGuid(this string? value)
            => Guid.TryParse(value, out var result) ? result : Guid.Empty;

        /// <summary>
        /// Read from the cache; on a miss call <paramref name="factory"/> and store the result.
        /// Null results are cached too, so a lookup that finds nothing does not hammer the
        /// database on every request.
        /// </summary>
        public static T? GetValue<T>(this IMemoryCache cache, string key, Func<T?> factory, MemoryCacheEntryOptions options)
        {
            if (cache.TryGetValue(key, out T? cached)) return cached;

            var value = factory();
            cache.Set(key, value, options);
            return value;
        }
    }
}
