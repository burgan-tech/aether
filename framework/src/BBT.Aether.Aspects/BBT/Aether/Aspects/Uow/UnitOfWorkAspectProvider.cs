using System;
using System.Collections.Generic;
using System.Reflection;
using PostSharp.Aspects;
using PostSharp.Aspects.Dependencies;

namespace BBT.Aether.Aspects;

/// <summary>
/// Aspect provider that automatically applies UnitOfWork aspect to methods of types 
/// implementing specific marker interfaces (e.g., IApplicationService).
/// This eliminates the need to manually add [UnitOfWork] attribute to every application service method.
/// </summary>
[Serializable]
[AspectTypeDependency(AspectDependencyAction.Order, AspectDependencyPosition.Before, typeof(UnitOfWorkAttribute))]
public class UnitOfWorkAspectProvider : IAspectProvider
{
    /// <summary>
    /// Gets the list of marker interface types that should have UnitOfWork applied.
    /// This list can be configured through UnitOfWorkConfiguration.
    /// </summary>
    private readonly static HashSet<string> MarkerInterfaceNames = new()
    {
        "BBT.Aether.Application.IApplicationService"
    };

    /// <summary>
    /// Default configuration for UnitOfWork when auto-applied.
    /// Can be customized by calling UnitOfWorkConfiguration.Configure().
    /// </summary>
    private static UnitOfWorkConfiguration Configuration = new();

    /// <summary>
    /// Configures the aspect provider with custom settings.
    /// </summary>
    static internal void Configure(UnitOfWorkConfiguration configuration)
    {
        Configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
    }

    /// <summary>
    /// Adds custom marker interfaces that should have UnitOfWork applied.
    /// </summary>
    static internal void AddMarkerInterface(Type interfaceType)
    {
        if (interfaceType == null)
            throw new ArgumentNullException(nameof(interfaceType));

        if (!interfaceType.IsInterface)
            throw new ArgumentException($"Type {interfaceType.FullName} must be an interface.", nameof(interfaceType));

        MarkerInterfaceNames.Add(interfaceType.FullName!);
    }

    /// <summary>
    /// Provides aspects to the specified target code element.
    /// Called by PostSharp during compilation to determine which aspects to apply.
    /// </summary>
    public IEnumerable<AspectInstance> ProvideAspects(object targetElement)
    {
        // 1) Assembly seviyesinden çağrıldıysa: tüm tipleri tara
        if (targetElement is Assembly asm)
        {
            foreach (var types in asm.GetTypes())
            {
                foreach (var ai in ProvideAspectsForType(types))
                {
                    yield return ai;
                }
            }
            yield break;
        }

        // 2) Type seviyesinden çağrıldıysa: doğrudan o tipi işle
        if (targetElement is Type type)
        {
            foreach (var ai in ProvideAspectsForType(type))
            {
                yield return ai;
            }
        }
    }
    
    private IEnumerable<AspectInstance> ProvideAspectsForType(Type type)
    {
        if (type == null || type.IsInterface || type.IsAbstract)
            yield break;

        // 🔧 Kapalı generic tipleri tanım seviyesine çevir
        // PostSharp requires aspects on generic type definitions (e.g., MyClass<T>)
        if (type.IsGenericType && !type.IsGenericTypeDefinition)
        {
            type = type.GetGenericTypeDefinition();
        }

        // Marker interface kontrolü
        if (!ImplementsMarkerInterface(type))
            yield break;

        // Public instance metotları tara
        var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance);

        foreach (var method in methods)
        {
            if (!IsCandidateMethod(method))
                continue;

            // 🔧 Metod tanımını elde et (generic definition'a map et)
            var methodDef = GetMethodDefinition(method);
            if (methodDef == null)
                continue;

            // Zaten UnitOfWork varsa geç
            if (methodDef.GetCustomAttribute<UnitOfWorkAttribute>() != null)
                continue;

            // Declaring type'da UnitOfWork varsa geç
            if (methodDef.DeclaringType?.GetCustomAttribute<UnitOfWorkAttribute>() != null)
                continue;

            // Aspect oluştur ve ver
            var aspect = CreateUnitOfWorkAspect(methodDef);
            yield return new AspectInstance(methodDef, aspect);
        }
    }

    /// <summary>
    /// Checks if a method is a candidate for UnitOfWork aspect application.
    /// </summary>
    private bool IsCandidateMethod(MethodInfo method)
    {
        // Skip abstract methods
        if (method.IsAbstract)
            return false;

        // Skip property getters/setters, event add/remove
        if (method.IsSpecialName)
            return false;

        // Skip System.Object methods (ToString, Equals, GetHashCode, GetType, etc.)
        // These are inherited virtual methods that should not have UnitOfWork
        if (method.DeclaringType == typeof(object))
            return false;

        // Skip methods from System namespace (external framework methods)
        if (method.DeclaringType?.Namespace?.StartsWith("System") == true &&
            method.DeclaringType?.Assembly == typeof(object).Assembly)
            return false;

        // Check configuration exclude rules
        if (Configuration.ShouldExcludeMethod(method))
            return false;

        return true;
    }

    /// <summary>
    /// Gets the method definition for generic methods and methods on generic types.
    /// PostSharp requires aspects to be applied to method definitions, not instances.
    /// </summary>
    private static MethodInfo? GetMethodDefinition(MethodInfo method)
    {
        // If method is on a generic type instance, map to definition type
        if (IsOnGenericTypeInstance(method))
        {
            var defType = method.DeclaringType!.GetGenericTypeDefinition();
            return FindMatchingMethodOnDefinition(defType, method);
        }

        // If method itself is a generic instance, get its definition
        if (method.IsGenericMethod && !method.IsGenericMethodDefinition)
        {
            return method.GetGenericMethodDefinition();
        }

        // Already a definition
        return method;
    }

    /// <summary>
    /// Checks if a method is declared on a generic type instance (closed generic).
    /// </summary>
    private static bool IsOnGenericTypeInstance(MethodInfo method)
    {
        return method.DeclaringType != null &&
               method.DeclaringType.IsGenericType &&
               !method.DeclaringType.IsGenericTypeDefinition;
    }

    /// <summary>
    /// Finds the matching method on the generic type definition.
    /// Maps a method from a closed generic type (e.g., Repository&lt;Order, Guid&gt;)
    /// to its definition on the open generic type (e.g., Repository&lt;T, TKey&gt;).
    /// </summary>
    private static MethodInfo? FindMatchingMethodOnDefinition(Type definitionType, MethodInfo instanceMethod)
    {
        var candidates = definitionType.GetMethods(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        // Get method signature info
        var genArity = instanceMethod.IsGenericMethod 
            ? instanceMethod.GetGenericArguments().Length 
            : 0;
        var paramCount = instanceMethod.GetParameters().Length;

        foreach (var candidate in candidates)
        {
            // Check name
            if (candidate.Name != instanceMethod.Name)
                continue;

            // Check generic arity
            var candidateGenArity = candidate.IsGenericMethod 
                ? candidate.GetGenericArguments().Length 
                : 0;
            if (candidateGenArity != genArity)
                continue;

            // Check parameter count
            var candidateParams = candidate.GetParameters();
            if (candidateParams.Length != paramCount)
                continue;

            // Check parameters match loosely
            if (ParametersLooselyMatch(candidateParams, instanceMethod.GetParameters()))
            {
                return candidate.IsGenericMethod 
                    ? candidate.GetGenericMethodDefinition() 
                    : candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// Loosely compares parameter arrays for generic method matching.
    /// Allows matching between open and closed generic parameters.
    /// </summary>
    private static bool ParametersLooselyMatch(ParameterInfo[] definitionParams, ParameterInfo[] instanceParams)
    {
        for (int i = 0; i < definitionParams.Length; i++)
        {
            var defType = definitionParams[i].ParameterType;
            var instType = instanceParams[i].ParameterType;

            // Exact match
            if (defType == instType)
                continue;

            // Both are generic parameters
            if (defType.IsGenericParameter && instType.IsGenericParameter)
                continue;

            // Both are generic types with same definition
            if (defType.IsGenericType && instType.IsGenericType)
            {
                var defGenType = defType.IsGenericTypeDefinition 
                    ? defType 
                    : defType.GetGenericTypeDefinition();
                var instGenType = instType.IsGenericTypeDefinition 
                    ? instType 
                    : instType.GetGenericTypeDefinition();

                if (defGenType == instGenType)
                    continue;
            }

            // No match
            return false;
        }

        return true;
    }

    /// <summary>
    /// Checks if the given type implements any of the configured marker interfaces.
    /// </summary>
    private bool ImplementsMarkerInterface(Type type)
    {
        var interfaces = type.GetInterfaces();
        foreach (var iface in interfaces)
        {
            // For generic interfaces, check both the full name and generic type definition
            var interfaceToCheck = iface.IsGenericType ? iface.GetGenericTypeDefinition() : iface;
            var interfaceName = interfaceToCheck.FullName;
            
            if (interfaceName != null && MarkerInterfaceNames.Contains(interfaceName))
                return true;
                
            // Also check non-generic version in case it's implemented
            if (iface.FullName != null && MarkerInterfaceNames.Contains(iface.FullName))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Creates a UnitOfWork aspect instance with configuration.
    /// </summary>
    private UnitOfWorkAttribute CreateUnitOfWorkAspect(MethodInfo method)
    {
        var aspect = new UnitOfWorkAttribute
        {
            IsTransactional = Configuration.IsTransactional,
            Scope = Configuration.Scope
        };

        if (Configuration.IsolationLevel.HasValue)
        {
            aspect.IsolationLevel = Configuration.IsolationLevel.Value;
        }

        // Apply method-specific configuration if available
        Configuration.ConfigureMethod?.Invoke(method, aspect);

        return aspect;
    }
}

