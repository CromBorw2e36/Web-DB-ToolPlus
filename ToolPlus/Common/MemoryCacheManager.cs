using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace ToolPlus.Common
{
    /// <summary>
    /// Wraps IMemoryCache to add Reset() — dropping the whole cache without a restart, which is
    /// what the reload-structure button needs — and GenerateKey().
    /// </summary>
    public sealed class MemoryCacheManager : IMemoryCache
    {
        private readonly IOptions<MemoryCacheOptions> _optionsAccessor;
        private IMemoryCache _inner;

        public MemoryCacheManager(IOptions<MemoryCacheOptions> optionsAccessor)
        {
            _optionsAccessor = optionsAccessor;
            _inner = new MemoryCache(_optionsAccessor);
        }

        public void Reset()
        {
            var old = _inner;
            _inner = new MemoryCache(_optionsAccessor);
            old.Dispose();
        }

        public void Dispose() => _inner.Dispose();

        public ICacheEntry CreateEntry(object key) => _inner.CreateEntry(key);

        public void Remove(object key) => _inner.Remove(key);

        public bool TryGetValue(object key, out object? value) => _inner.TryGetValue(key, out value);

        public string GenerateKey(string seed) => Utility.GetSHA256("memory-cache-key-" + seed);
    }
}
