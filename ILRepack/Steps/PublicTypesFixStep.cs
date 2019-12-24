using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;

namespace ILRepacking.Steps
{
    internal class PublicTypesFixStep : IRepackStep
    {
        private readonly ILogger _logger;
        private readonly IRepackContext _repackContext;
        private readonly IRepackImporter _repackImporter;
        private readonly RepackOptions _repackOptions;
        private readonly HashSet<TypeDefinition> _visitedTypes = new HashSet<TypeDefinition>();

        public PublicTypesFixStep(
            ILogger logger,
            IRepackContext repackContext,
            IRepackImporter repackImporter,
            RepackOptions repackOptions)
        {
            _logger = logger;
            _repackContext = repackContext;
            _repackImporter = repackImporter;
            _repackOptions = repackOptions;
        }

        public void Perform()
        {
            _logger.Info("Processing public types tree");

            var publicTypes = _repackContext.TargetAssemblyMainModule.Types.Where(t => t.IsPublic);

            foreach (var type in publicTypes)
            {
                EnsureDependencies(type);
            }
        }

        private void EnsureDependencies(TypeDefinition type)
        {
            if (type == null) return;

            if (!_visitedTypes.Add(type)) return;

            if (type.HasFields)
            {
                foreach (var field in type.Fields)
                {
                    EnsureDependencies(field.FieldType);
                }
            }

            if (type.HasProperties)
            {
                foreach (var property in type.Properties)
                {
                    EnsureDependencies(property.PropertyType);
                }
            }

            if (type.HasEvents)
            {
                foreach (var evt in type.Events)
                {
                    EnsureDependencies(evt.EventType);
                }
            }

            if (type.HasMethods)
            {
                foreach (var method in type.Methods)
                {
                    foreach (var parameter in method.Parameters)
                    {
                        EnsureDependencies(parameter.ParameterType);
                    }
                    EnsureDependencies(method.ReturnType);
                }
            }

            EnsureDependencies(type.BaseType);

            if (type.IsPublic) return;

            type.IsPublic = true;
        }

        private void EnsureDependencies(TypeReference type)
        {
            if (type == null) return;

            if (type.IsGenericInstance && type is GenericInstanceType genericType)
            {
                foreach (var argument in genericType.GenericArguments)
                {
                    EnsureDependencies(argument);
                }
            }

            if (type.Module != _repackContext.TargetAssemblyMainModule) return;

            var definition = type.Resolve();

            EnsureDependencies(definition);
        }
    }
}
