using System.Threading.Tasks;
using BBT.Aether.Application;
using BBT.Aether.Domain.Entities;
using Microsoft.AspNetCore.Mvc;

namespace BBT.Aether.AspNetCore.Controllers;

public abstract class AbstractKeyEntityCrudController<TEntity, TKey>(ICrudEntityAppService<TEntity, TKey> service)
    : ControllerBase
    where TEntity : class, IEntity<TKey>
{
    protected ICrudEntityAppService<TEntity, TKey> CrudAppService { get; } = service;

    [HttpPost]
    public virtual async Task<IActionResult> CreateAsync(TEntity input)
    {
        var item = await CrudAppService.CreateAsync(input);
        return Ok(item);
    }

    [HttpPut("{id}")]
    public virtual async Task<IActionResult> UpdateAsync(TKey id, TEntity input)
    {
        var item = await CrudAppService.UpdateAsync(id, input);
        return Ok(item);
    }

    [HttpDelete("{id}")]
    public virtual async Task<IActionResult> DeleteAsync(TKey id)
    {
        await CrudAppService.DeleteAsync(id);
        return Ok();
    }
}