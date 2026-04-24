using System;
using System.Collections.Generic;
using BBT.Aether.Application.Dtos;

namespace BBT.Aether.Application.Pagination;

/// <summary>
/// <see cref="IPaginationLinkGenerator"/> decorator that pins the base URL to a caller-supplied
/// value, bypassing the ambient <see cref="IPaginationContext"/>.<see cref="IPaginationContext.BaseUrl"/>.
/// Query parameters from the context are still preserved so filters and sort order survive on links.
///
/// Constructed via <see cref="PaginationLinkGenerator.Relative"/> (empty base URL) or
/// <see cref="PaginationLinkGenerator.WithBaseUrl"/> (explicit base URL); not registered in DI.
/// </summary>
internal sealed class FixedBaseUrlPaginationLinkGenerator : IPaginationLinkGenerator
{
    private readonly IPaginationContext _context;
    private readonly string _baseUrl;

    public FixedBaseUrlPaginationLinkGenerator(IPaginationContext context, string baseUrl)
    {
        _context = context;
        _baseUrl = baseUrl;
    }

    public IPaginationLinkGenerator Relative()
        => new FixedBaseUrlPaginationLinkGenerator(_context, baseUrl: string.Empty);

    public IPaginationLinkGenerator WithBaseUrl(string baseUrl)
    {
        if (baseUrl is null)
        {
            throw new ArgumentNullException(nameof(baseUrl));
        }
        return new FixedBaseUrlPaginationLinkGenerator(_context, baseUrl);
    }

    public PaginationLinks GenerateLinks<T>(HateoasPagedList<T> pagedList, string routePath)
        => PaginationLinkGenerator.BuildLinks(_baseUrl, pagedList.CurrentPage, pagedList.PageSize,
            pagedList.HasNext, pagedList.HasPrevious, routePath, _context.QueryParameters);

    public PaginationLinks GenerateLinks<T>(PagedList<T> pagedList, string routePath)
        => PaginationLinkGenerator.BuildLinks(_baseUrl, pagedList.CurrentPage, pagedList.PageSize,
            pagedList.HasNext, pagedList.HasPrevious, routePath, _context.QueryParameters);

    public HateoasPagedResultDto<TDto> CreateHateoasResult<TEntity, TDto>(
        HateoasPagedList<TEntity> pagedList,
        IReadOnlyList<TDto> items,
        string routePath)
        => new(items, GenerateLinks(pagedList, routePath));

    public HateoasPagedResultDto<TDto> CreateHateoasResult<TEntity, TDto>(
        PagedList<TEntity> pagedList,
        IReadOnlyList<TDto> items,
        string routePath)
        => new(items, GenerateLinks(pagedList, routePath));
}
