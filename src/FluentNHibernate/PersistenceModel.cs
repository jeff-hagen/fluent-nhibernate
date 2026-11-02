using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Xml;
using System.Xml.Serialization;
using FluentNHibernate.Conventions;
using FluentNHibernate.Diagnostics;
using FluentNHibernate.Mapping;
using FluentNHibernate.Mapping.Providers;
using FluentNHibernate.MappingModel;
using FluentNHibernate.MappingModel.ClassBased;
using FluentNHibernate.MappingModel.Output;
using FluentNHibernate.Utils;
using FluentNHibernate.Visitors;
using NHibernate.Cfg;
using NHibernate.Cfg.MappingSchema;

namespace FluentNHibernate;

public class PersistenceModel
{
    protected readonly IList<IMappingProvider> classProviders = new List<IMappingProvider>();
    protected readonly IList<IFilterDefinition> filterDefinitions = new List<IFilterDefinition>();
    protected readonly IIndeterminateSubclassMappingProviderCollection subclassProviders = new IndeterminateSubclassMappingProviderCollection();
    protected readonly IList<IExternalComponentMappingProvider> componentProviders = new List<IExternalComponentMappingProvider>();
    protected readonly IList<IComponentReferenceResolver> componentResolvers = new List<IComponentReferenceResolver>
    {
        new ComponentMapComponentReferenceResolver()
    };

    readonly IList<IMappingModelVisitor> visitors = new List<IMappingModelVisitor>();
    public IConventionFinder Conventions { get; }
    public bool MergeMappings { get; set; }
    IEnumerable<HibernateMapping> compiledMappings;
    ValidationVisitor validationVisitor;
    public PairBiDirectionalManyToManySidesDelegate BiDirectionalManyToManyPairer { get; set; }

    IDiagnosticMessageDispatcher diagnosticDispatcher = new DefaultDiagnosticMessageDispatcher();
    protected IDiagnosticLogger log = new NullDiagnosticsLogger();

    public PersistenceModel(IConventionFinder conventionFinder)
    {
        BiDirectionalManyToManyPairer = (c,o,w) => {};
        Conventions = conventionFinder;

        visitors.Add(new SeparateSubclassVisitor(subclassProviders));
        visitors.Add(new ComponentReferenceResolutionVisitor(componentResolvers, componentProviders));
        visitors.Add(new RelationshipPairingVisitor(BiDirectionalManyToManyPairer));
        visitors.Add(new ManyToManyTableNameVisitor());
        visitors.Add(new ConventionVisitor(Conventions));
        visitors.Add(new ComponentColumnPrefixVisitor());
        visitors.Add(new RelationshipKeyPairingVisitor());
        visitors.Add((validationVisitor = new ValidationVisitor()));
    }

    public PersistenceModel(): this(new DefaultConventionFinder())
    {}

    public void SetLogger(IDiagnosticLogger logger)
    {
        log = logger;
        Conventions.SetLogger(logger);
    }

    protected void AddMappingsFromThisAssembly()
    {
        var assembly = FindTheCallingAssembly();
        AddMappingsFromAssembly(assembly);
    }

    public void AddMappingsFromAssembly(Assembly assembly)
    {
        AddMappingsFromSource(new AssemblyTypeSource(assembly));
    }

    public void AddMappingsFromSource(ITypeSource source)
    {
        var types = source.GetTypes()
            .Where(x => IsMappingOf<IMappingProvider>(x) ||
                        IsMappingOf<IIndeterminateSubclassMappingProvider>(x) ||
                        IsMappingOf<IExternalComponentMappingProvider>(x) ||
                        IsMappingOf<IFilterDefinition>(x));

        foreach (var type in types)
            Add(type);

        log.LoadedFluentMappingsFromSource(source);
    }

    static Assembly FindTheCallingAssembly()
    {
        StackTrace trace = new StackTrace(false);

        Assembly thisAssembly = Assembly.GetExecutingAssembly();
        Assembly callingAssembly = null;
        for (int i = 0; i < trace.FrameCount; i++)
        {
            StackFrame frame = trace.GetFrame(i);
            Assembly assembly = frame.GetMethod().DeclaringType.Assembly;
            if (assembly != thisAssembly)
            {
                callingAssembly = assembly;
                break;
            }
        }
        return callingAssembly;
    }

    public void Add(IMappingProvider provider)
    {
        classProviders.Add(provider);
    }

    public void Add(IIndeterminateSubclassMappingProvider provider)
    {
        subclassProviders.Add(provider);
    }

    public void Add(IFilterDefinition definition)
    {
        filterDefinitions.Add(definition);
    }

    public void Add(IExternalComponentMappingProvider provider)
    {
        componentProviders.Add(provider);
    }

    public void Add(Type type)
    {
        var mapping = type.InstantiateUsingParameterlessConstructor();

        if (mapping is IMappingProvider)
        {
            log.FluentMappingDiscovered(type);
            Add((IMappingProvider)mapping);
        }
        else if (mapping is IIndeterminateSubclassMappingProvider)
        {
            log.FluentMappingDiscovered(type);
            Add((IIndeterminateSubclassMappingProvider)mapping);
        }
        else if (mapping is IFilterDefinition)
            Add((IFilterDefinition)mapping);
        else if (mapping is IExternalComponentMappingProvider)
        {
            log.FluentMappingDiscovered(type);
            Add((IExternalComponentMappingProvider)mapping);
        }
        else
            throw new InvalidOperationException("Unsupported mapping type '" + type.FullName + "'");
    }

    bool IsMappingOf<T>(Type type)
    {
        return !type.IsGenericType && typeof(T).IsAssignableFrom(type);
    }

    public virtual IEnumerable<HibernateMapping> BuildMappings()
    {
        var hbms = new List<HibernateMapping>();

        if (MergeMappings)
            BuildSingleMapping(hbms.Add);
        else
            BuildSeparateMappings(hbms.Add);

        ApplyVisitors(hbms);

        log.Flush();

        return hbms;
    }

    void BuildSeparateMappings(Action<HibernateMapping> add)
    {
        foreach (var classMap in classProviders)
        {
            var hbm = classMap.GetHibernateMapping();

            hbm.AddClass(classMap.GetClassMapping());

            add(hbm);
        }

        foreach (var filterDefinition in filterDefinitions)
        {
            var hbm = filterDefinition.GetHibernateMapping();
            hbm.AddFilter(filterDefinition.GetFilterMapping());
            add(hbm);
        }
    }

    void BuildSingleMapping(Action<HibernateMapping> add)
    {
        var hbm = new HibernateMapping();

        foreach (var classMap in classProviders)
        {
            hbm.AddClass(classMap.GetClassMapping());
        }
        foreach (var filterDefinition in filterDefinitions)
        {
            hbm.AddFilter(filterDefinition.GetFilterMapping());
        }

        if (hbm.Classes.Any())
            add(hbm);
    }

    void ApplyVisitors(IEnumerable<HibernateMapping> mappings)
    {
        foreach (var visitor in visitors)
            visitor.Visit(mappings);
    }

    void EnsureMappingsBuilt()
    {
        if (compiledMappings is not null) return;

        compiledMappings = BuildMappings();
    }

    protected virtual string GetMappingFileName()
    {
        return "FluentMappings.hbm.xml";
    }

    string DetermineMappingFileName(HibernateMapping mapping)
    {
        if (MergeMappings)
            return GetMappingFileName();

        if (mapping.Classes.Any())
            return mapping.Classes.First().Type.FullName + ".hbm.xml";

        return "filter-def." + mapping.Filters.First().Name + ".hbm.xml";
    }

    public void WriteMappingsTo(string folder)
    {
        WriteMappingsTo(mapping => new XmlTextWriter(Path.Combine(folder, DetermineMappingFileName(mapping)), Encoding.Default), true);
    }

    public void WriteMappingsTo(TextWriter writer)
    {
        WriteMappingsTo( _ => new XmlTextWriter(writer), false);
    }

    public void WriteMappingsTo(Func<HibernateMapping, XmlTextWriter> writerBuilder, bool shouldDispose)
    {
        EnsureMappingsBuilt();

        foreach (var mapping in compiledMappings)
        {
            var serializer = new MappingXmlSerializer();
            var document = serializer.Serialize(mapping);

            XmlTextWriter xmlWriter = null;

            try
            {
                xmlWriter = writerBuilder(mapping);
                xmlWriter.Formatting = Formatting.Indented;
                document.WriteTo(xmlWriter);
            }
            finally
            {
                if(shouldDispose && xmlWriter is not null)
                    xmlWriter.Close();
            }
        }
    }

    /// <summary>
    /// The name NHibernate's <see cref="Configuration.AddDocument(XmlDocument)"/> gave every Fluent document; kept so
    /// <see cref="Configuration.BeforeBindMapping"/> / <see cref="Configuration.AfterBindMapping"/> handlers and
    /// mapping-error messages see the same <c>FileName</c> as before.
    /// </summary>
    const string DocumentName = "(XmlDocument)";

    static readonly XmlSerializer HbmMappingSerializer = new XmlSerializer(typeof(HbmMapping));

    public virtual void Configure(Configuration cfg)
    {
        EnsureMappingsBuilt();

        foreach (var mapping in compiledMappings.Where(m => !m.Classes.Any()))
            cfg.AddDeserializedMapping(ToHbmMapping(mapping), DocumentName);

        foreach (var mapping in compiledMappings.Where(m => m.Classes.Any()))
        {
            if (cfg.GetClassMapping(mapping.Classes.First().Type) is null)
                cfg.AddDeserializedMapping(ToHbmMapping(mapping), DocumentName);
        }
    }

    /// <summary>
    /// Deserializes the Fluent-rendered document straight into the <see cref="HbmMapping"/> NHibernate binds, skipping
    /// the Save / XSD-validating re-parse / OuterXml round trip <see cref="Configuration.AddDocument(XmlDocument)"/>
    /// does per document. Bypassing NHibernate's mappings queue is safe: Fluent never writes an <c>extends</c>
    /// attribute, so no document ever waits on another.
    /// </summary>
    static HbmMapping ToHbmMapping(HibernateMapping mapping)
    {
        var document = new MappingXmlSerializer().Serialize(mapping);

        using var reader = new MappingNamespaceNodeReader(document.DocumentElement);
        return (HbmMapping)HbmMappingSerializer.Deserialize(reader);
    }

    /// <summary>
    /// Fluent builds its DOM in the empty namespace and writes <c>xmlns="urn:nhibernate-mapping-2.2"</c> as a plain
    /// attribute, which only becomes the elements' namespace once the text is re-parsed. This reader applies that
    /// namespace in place, returning the NameTable-atomized string the generated serializer compares by reference.
    /// </summary>
    sealed class MappingNamespaceNodeReader(XmlNode node) : XmlNodeReader(node)
    {
        string mappingNamespace;

        public override string NamespaceURI =>
            NodeType is XmlNodeType.Element or XmlNodeType.EndElement && base.NamespaceURI.Length == 0
                ? mappingNamespace ??= NameTable.Add("urn:nhibernate-mapping-2.2")
                : base.NamespaceURI;
    }

    public bool ContainsMapping(Type type)
    {
        return classProviders.Any(x => x.GetType() == type) ||
               filterDefinitions.Any(x => x.GetType() == type) ||
               subclassProviders.Any(x => x.GetType() == type) ||
               componentProviders.Any(x => x.GetType() == type);
    }

    /// <summary>
    /// Gets or sets whether validation of mappings is performed.
    /// </summary>
    public bool ValidationEnabled
    {
        get => validationVisitor.Enabled;
        set => validationVisitor.Enabled = value;
    }

    internal void ImportProviders(PersistenceModel model)
    {
        foreach (var provider in model.classProviders)
        {
            if (!classProviders.Contains(provider))
                classProviders.Add(provider);
        }

        foreach (var provider in model.subclassProviders)
        {
            if (!subclassProviders.Contains(provider)) 
                subclassProviders.Add(provider);
        }

        foreach (var provider in model.componentProviders)
        {
            if (!componentProviders.Contains(provider))
                componentProviders.Add(provider);
        }
    }
}

public interface IMappingProvider
{
    ClassMapping GetClassMapping();
    // HACK: In place just to keep compatibility until verdict is made
    HibernateMapping GetHibernateMapping();
    IEnumerable<Member> GetIgnoredProperties();
}

public class PassThroughMappingProvider(ClassMapping mapping) : IMappingProvider
{
    public ClassMapping GetClassMapping()
    {
        return mapping;
    }

    public HibernateMapping GetHibernateMapping()
    {
        return new HibernateMapping();
    }

    public IEnumerable<Member> GetIgnoredProperties()
    {
        return Array.Empty<Member>();
    }
}
