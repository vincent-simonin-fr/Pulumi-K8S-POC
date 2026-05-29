namespace Inventory.Application.Common;

/// <summary>
/// Cles de cache partagees entre les couches Application et Infrastructure.
/// Centralise les noms de cles pour eviter les litteraux dupliques.
/// </summary>
public static class CacheKeys
{
    /// <summary>
    /// Liste complete des produits — invalider des que le stock change
    /// (reservation creee ou expiree).
    /// TTL de secours : <see cref="CacheTtl.Products"/>.
    /// </summary>
    public const string ProductsAll = "products:all";
}

public static class CacheTtl
{
    /// <summary>Duree de vie du cache produits. Configurable via Cache:ProductsTtlSeconds.</summary>
    public static readonly TimeSpan Products = TimeSpan.FromSeconds(30);
}
