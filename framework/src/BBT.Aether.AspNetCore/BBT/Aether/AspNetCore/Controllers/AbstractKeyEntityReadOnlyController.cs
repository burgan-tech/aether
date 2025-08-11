using System.Threading.Tasks;
using BBT.Aether.Application;
using BBT.Aether.Domain.Entities;
using BBT.Aether.Domain.Repositories;
using Microsoft.AspNetCore.Mvc;

namespace BBT.Aether.AspNetCore.Controllers;

public abstract class AbstractKeyEntityReadOnlyController<TEntity, TKey>(
    IReadOnlyEntityAppService<TEntity, TKey> service
    ) : ControllerBase
    where TEntity : class, IEntity<TKey>
{
    protected IReadOnlyEntityAppService<TEntity, TKey> ReadOnlyAppService { get; } = service;
    
    [HttpGet("{id}")]
    public virtual async Task<IActionResult> GetAsync(TKey id)
    {
        var item = await ReadOnlyAppService.GetAsync(id);
        return Ok(item);
    }

    [HttpGet("all")]
    public virtual async Task<IActionResult> GetListAsync()
    {
        var items = await ReadOnlyAppService.GetListAsync();
        return Ok(items);
    }

    [HttpGet("paged")]
    public virtual async Task<IActionResult> GetPagedListAsync(PaginationParameters input)
    {
        var items = await ReadOnlyAppService.GetPagedListAsync(input);
        return Ok(items);
    }
}