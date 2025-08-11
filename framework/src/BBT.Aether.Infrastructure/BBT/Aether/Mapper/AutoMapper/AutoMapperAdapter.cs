using AutoMapper;

namespace BBT.Aether.Mapper.AutoMapper;

public class AutoMapperAdapter(IMapper mapper) : IObjectMapper
{
    public TDestination Map<TSource, TDestination>(TSource source)
    {
        return mapper.Map<TSource, TDestination>(source);
    }

    public void Map<TSource, TDestination>(TSource source, TDestination destination)
    {
        mapper.Map(source, destination);
    }
}

public class AutoMapperAdapter<TSource, TDestination>(IMapper mapper) : IObjectMapper<TSource, TDestination>
{
    public TDestination Map(TSource source)
    {
        return mapper.Map<TSource, TDestination>(source);
    }

    public TDestination Map(TSource source, TDestination destination)
    {
        return mapper.Map(source, destination);
    }
}