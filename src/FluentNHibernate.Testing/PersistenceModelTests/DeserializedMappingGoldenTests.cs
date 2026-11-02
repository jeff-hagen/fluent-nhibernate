using System;
using System.Collections;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Serialization;
using FluentNHibernate.Mapping;
using FluentNHibernate.MappingModel.Output;
using NHibernate;
using NHibernate.Cfg;
using NHibernate.Cfg.MappingSchema;
using NHibernate.Engine;
using FilterDefinition = FluentNHibernate.Mapping.FilterDefinition;
using NHibernate.Persister.Collection;
using NHibernate.Persister.Entity;
using NHibernate.SqlCommand;
using NHibernate.SqlTypes;
using NHibernate.Type;
using NHibernate.UserTypes;
using NHibernate.Util;
using NUnit.Framework;
using static FluentNHibernate.Testing.Cfg.SQLiteFrameworkConfigurationFactory;

namespace FluentNHibernate.Testing.PersistenceModelTests;

/// <summary>
/// Golden tests for <see cref="PersistenceModel.Configure"/> handing NHibernate a deserialized <see cref="HbmMapping"/>
/// instead of an XmlDocument: the result must be indistinguishable from NHibernate's own AddDocument path, which saves
/// the document, re-parses it with XSD validation and deserializes its OuterXml.
/// </summary>
[TestFixture]
public class DeserializedMappingGoldenTests
{
    static readonly XmlSerializer HbmSerializer = new XmlSerializer(typeof(HbmMapping));

    [Test]
    public void Each_document_binds_the_same_HbmMapping_as_the_AddDocument_path()
    {
        var legacy = Capture(new LegacyAddDocumentPersistenceModel());
        var deserialized = Capture(new PersistenceModel());

        Assert.That(deserialized.Documents.Select(d => d.FileName), Is.EqualTo(legacy.Documents.Select(d => d.FileName)));
        Assert.That(deserialized.Documents.Select(d => d.Xml), Is.EqualTo(legacy.Documents.Select(d => d.Xml)));
        Assert.That(deserialized.AfterBindCount, Is.EqualTo(legacy.AfterBindCount));
    }

    [Test]
    public void The_fixture_maps_exercise_every_shape_the_golden_test_claims()
    {
        var xml = string.Join("\n", Capture(new PersistenceModel()).Documents.Select(d => d.Xml));

        foreach (var element in new[]
                 {
                     "<filter-def ", "<filter-param ", "<filter ", "<subclass ", "<joined-subclass ", "<join ",
                     "<component ", "<bag ", "<set ", "<map ", "<list ", "<many-to-many ", "<one-to-many ",
                     "<many-to-one ", "<composite-id", "<key-property ", "<version ", "<natural-id", "<discriminator ",
                     "<cache ", "formula=", "persister=", nameof(GoldenFlagsUserType),
                 })
        {
            Assert.That(xml, Does.Contain(element), "fixture maps no longer cover " + element);
        }
    }

    [Test]
    public void Both_paths_build_identical_persister_sql_and_schema()
    {
        var legacy = Describe(new LegacyAddDocumentPersistenceModel());
        var deserialized = Describe(new PersistenceModel());

        Assert.That(legacy.Keys.Count(k => k.StartsWith("entity ")), Is.EqualTo(9));
        Assert.That(legacy.Keys.Count(k => k.StartsWith("collection ")), Is.EqualTo(5));
        Assert.That(deserialized.Keys, Is.EqualTo(legacy.Keys));
        foreach (var key in legacy.Keys)
            Assert.That(deserialized[key], Is.EqualTo(legacy[key]), key);
    }

    [Test]
    public void The_comparison_detects_a_dropped_filter()
    {
        // Proves the golden comparison has teeth: the same document with its filter-def removed must not compare equal.
        var doc = Capture(new PersistenceModel()).Mappings.First(m => m.filterdef is { Length: > 0 });
        var original = Serialize(doc);

        doc.filterdef = [];

        Assert.That(Serialize(doc), Is.Not.EqualTo(original));
    }

    static Captured Capture(PersistenceModel model)
    {
        var captured = new Captured();
        var cfg = NewConfiguration();
        cfg.BeforeBindMapping += (_, args) =>
        {
            captured.Mappings.Add(args.Mapping);
            captured.Documents.Add((args.FileName, Serialize(args.Mapping)));
        };
        cfg.AfterBindMapping += (_, _) => captured.AfterBindCount++;

        AddFixtureMaps(model);
        model.Configure(cfg);

        Assert.That(captured.Documents, Is.Not.Empty);
        return captured;
    }

    static SortedDictionary<string, string> Describe(PersistenceModel model)
    {
        var cfg = NewConfiguration();
        AddFixtureMaps(model);
        model.Configure(cfg);

        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var dialect = NHibernate.Dialect.Dialect.GetDialect(cfg.Properties);
        result["(schema)"] = string.Join("\n", cfg.GenerateSchemaCreationScript(dialect));
        result["(filters)"] = string.Join("\n", cfg.FilterDefinitions.OrderBy(f => f.Key)
            .Select(f => f.Key + ":" + f.Value.DefaultFilterCondition + ":"
                         + string.Join(",", f.Value.ParameterNames.Select(p => p + "=" + f.Value.GetParameterType(p).Name))));

        using var factory = (ISessionFactoryImplementor)cfg.BuildSessionFactory();
        foreach (var persister in factory.GetAllClassMetadata().Values.Cast<IEntityPersister>())
            result["entity " + persister.EntityName] = NormalizeFormulaAliases(DescribeFields(persister));
        foreach (var persister in factory.GetAllCollectionMetadata().Values.Cast<ICollectionPersister>())
            result["collection " + persister.Role] = NormalizeFormulaAliases(DescribeFields(persister));

        return result;
    }

    /// <summary>NHibernate numbers formula aliases from a process-wide static counter, so they differ between any two builds.</summary>
    static string NormalizeFormulaAliases(string description) =>
        Regex.Replace(description, @"\bformula\d+_", "formulaN_", RegexOptions.None, TimeSpan.FromSeconds(1));

    /// <summary>
    /// Every SQL string, column/table name, type name, flag and count NHibernate computed into the persister (and its
    /// filter helpers, which hold the filter conditions).
    /// </summary>
    static string DescribeFields(object target)
    {
        var sb = new StringBuilder();
        for (var type = target.GetType(); type is not null && type != typeof(object); type = type.BaseType)
        {
            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                         .OrderBy(f => f.Name, StringComparer.Ordinal))
            {
                var value = Render(field.GetValue(target));
                if (value is not null)
                    sb.Append(type.Name).Append('.').Append(field.Name).Append('=').Append(value).Append('\n');
            }
        }

        return sb.ToString();
    }

    static string Render(object value) =>
        value switch
        {
            null => "null",
            string s => s,
            SqlString s => s.ToString(),
            SqlCommandInfo c => c.Text + " [" + string.Join(",", c.ParameterTypes.Select(t => t.ToString())) + "]",
            Enum or bool or int or long => value.ToString(),
            IType t => t.Name,
            FilterHelper => "{" + DescribeFields(value) + "}",
            IEnumerable e when value.GetType().IsArray => RenderArray(e),
            _ => null,
        };

    static string RenderArray(IEnumerable items)
    {
        var rendered = items.Cast<object>().Select(Render).ToList();
        return rendered.Any(r => r is null) ? null : "[" + string.Join("|", rendered) + "]";
    }

    static string Serialize(HbmMapping mapping)
    {
        using var writer = new StringWriter();
        HbmSerializer.Serialize(writer, mapping);
        return writer.ToString();
    }

    static Configuration NewConfiguration()
    {
        var cfg = new Configuration();
        CreateStandardInMemoryConfiguration().ConfigureProperties(cfg);
        return cfg;
    }

    static void AddFixtureMaps(PersistenceModel model)
    {
        model.Add(new GoldenTenantFilter());
        model.Add(new GoldenParentMap());
        model.Add(new GoldenChildMap());
        model.Add(new GoldenTagMap());
        model.Add(new GoldenAnimalMap());
        model.Add(new GoldenDogMap());
        model.Add(new GoldenCatMap());
        model.Add(new GoldenLineMap());
        model.Add(new GoldenDocumentMap());
        model.Add(new GoldenImageMap());
    }

    class Captured
    {
        public List<HbmMapping> Mappings { get; } = [];
        public List<(string FileName, string Xml)> Documents { get; } = [];
        public int AfterBindCount { get; set; }
    }

    /// <summary>The 4.0.2 Configure body verbatim: every document goes through NHibernate's AddDocument.</summary>
    class LegacyAddDocumentPersistenceModel : PersistenceModel
    {
        public override void Configure(Configuration cfg)
        {
            var mappings = BuildMappings().ToList();

            foreach (var mapping in mappings.Where(m => !m.Classes.Any()))
                cfg.AddDocument(new MappingXmlSerializer().Serialize(mapping));

            foreach (var mapping in mappings.Where(m => m.Classes.Any()))
            {
                if (cfg.GetClassMapping(mapping.Classes.First().Type) is null)
                    cfg.AddDocument(new MappingXmlSerializer().Serialize(mapping));
            }
        }
    }

    public class GoldenTenantFilter : FilterDefinition
    {
        public GoldenTenantFilter()
        {
            WithName("tenant").WithCondition("TenantId = :tenantId").AddParameter("tenantId", NHibernateUtil.Int32);
        }
    }

    public class GoldenParent
    {
        public virtual int Id { get; set; }
        public virtual int Version { get; set; }
        public virtual string Code { get; set; }
        public virtual string Name { get; set; }
        public virtual int NameLength { get; set; }
        public virtual int TenantId { get; set; }
        public virtual string Extra { get; set; }
        public virtual GoldenAddress Address { get; set; }
        public virtual GoldenParent Owner { get; set; }
        public virtual IList<GoldenChild> Children { get; set; }
        public virtual ISet<GoldenTag> Tags { get; set; }
        public virtual IDictionary<string, string> Attributes { get; set; }
        public virtual IList<string> Notes { get; set; }
    }

    public class GoldenAddress
    {
        public virtual string Street { get; set; }
        public virtual string City { get; set; }
    }

    public class GoldenChild
    {
        public virtual int Id { get; set; }
        public virtual string Name { get; set; }
        public virtual int TenantId { get; set; }
        public virtual GoldenParent Parent { get; set; }
    }

    public class GoldenTag
    {
        public virtual int Id { get; set; }
        public virtual string Label { get; set; }
        public virtual ISet<GoldenParent> Parents { get; set; }
    }

    public class GoldenAnimal
    {
        public virtual int Id { get; set; }
        public virtual string Name { get; set; }
    }

    public class GoldenDog : GoldenAnimal
    {
        public virtual string Breed { get; set; }
    }

    public class GoldenCat : GoldenAnimal
    {
        public virtual int Lives { get; set; }
    }

    public class GoldenLine
    {
        public virtual int OrderId { get; set; }
        public virtual int LineNo { get; set; }
        public virtual decimal Amount { get; set; }

        public override bool Equals(object obj) => obj is GoldenLine other && other.OrderId == OrderId && other.LineNo == LineNo;

        public override int GetHashCode() => (OrderId * 397) ^ LineNo;
    }

    class GoldenParentMap : ClassMap<GoldenParent>
    {
        public GoldenParentMap()
        {
            Table("GoldenParent");
            Persister<SingleTableEntityPersister>();
            DynamicUpdate();
            BatchSize(25);
            Cache.ReadWrite().Region("golden");
            Id(x => x.Id).GeneratedBy.Native();
            Version(x => x.Version);
            NaturalId().Property(x => x.Code);
            Map(x => x.Name).Length(200).Not.Nullable().Index("IX_GoldenParent_Name");
            Map(x => x.NameLength).Formula("length(Name)");
            Map(x => x.TenantId).Not.Update();
            Component(x => x.Address, c =>
            {
                c.Map(x => x.Street);
                c.Map(x => x.City).Column("CityName");
            });
            References(x => x.Owner).Column("OwnerId").LazyLoad();
            HasMany(x => x.Children).KeyColumn("ParentId").Inverse().Cascade.AllDeleteOrphan()
                .Where("Name is not null").ApplyFilter<GoldenTenantFilter>("TenantId = :tenantId");
            HasManyToMany(x => x.Tags).AsSet().Table("GoldenParentTag").ParentKeyColumn("ParentId").ChildKeyColumn("TagId")
                .Cascade.SaveUpdate();
            HasMany(x => x.Attributes).Table("GoldenParentAttribute").KeyColumn("ParentId").AsMap<string>("AttrKey")
                .Element("AttrValue");
            HasMany(x => x.Notes).Table("GoldenParentNote").KeyColumn("ParentId").AsList(i => i.Column("Position"))
                .Element("Note");
            Join("GoldenParentExtra", j =>
            {
                j.KeyColumn("ParentId");
                j.Optional();
                j.Map(x => x.Extra);
            });
            ApplyFilter<GoldenTenantFilter>("TenantId = :tenantId");
        }
    }

    class GoldenChildMap : ClassMap<GoldenChild>
    {
        public GoldenChildMap()
        {
            Id(x => x.Id).GeneratedBy.Assigned();
            Map(x => x.Name);
            Map(x => x.TenantId);
            References(x => x.Parent).Column("ParentId").Not.Nullable();
            ApplyFilter<GoldenTenantFilter>();
        }
    }

    class GoldenTagMap : ClassMap<GoldenTag>
    {
        public GoldenTagMap()
        {
            ReadOnly();
            Id(x => x.Id);
            Map(x => x.Label).Unique();
            HasManyToMany(x => x.Parents).AsSet().Table("GoldenParentTag").ParentKeyColumn("TagId").ChildKeyColumn("ParentId")
                .Inverse();
        }
    }

    class GoldenAnimalMap : ClassMap<GoldenAnimal>
    {
        public GoldenAnimalMap()
        {
            Id(x => x.Id);
            Map(x => x.Name);
            DiscriminateSubClassesOnColumn("Kind").Length(10);
        }
    }

    class GoldenDogMap : SubclassMap<GoldenDog>
    {
        public GoldenDogMap()
        {
            DiscriminatorValue("dog");
            Map(x => x.Breed);
        }
    }

    class GoldenCatMap : SubclassMap<GoldenCat>
    {
        public GoldenCatMap()
        {
            DiscriminatorValue("cat");
            Map(x => x.Lives);
        }
    }

    public class GoldenDocument
    {
        public virtual Guid Id { get; set; }
        public virtual DateTime Created { get; set; }
    }

    public class GoldenImage : GoldenDocument
    {
        public virtual string Title { get; set; }
        public virtual bool[] Flags { get; set; }
    }

    class GoldenDocumentMap : ClassMap<GoldenDocument>
    {
        public GoldenDocumentMap()
        {
            Id(x => x.Id).Column("[Guid]").GeneratedBy.GuidComb();
            Map(x => x.Created).Not.Nullable();
        }
    }

    class GoldenImageMap : SubclassMap<GoldenImage>
    {
        public GoldenImageMap()
        {
            Table("[GoldenImage]");
            KeyColumn("[Guid]");
            Map(x => x.Title).Length(100).Not.Nullable();
            Map(x => x.Flags).CustomType<GoldenFlagsUserType>().Columns.Add("IsIcon", "IsPromo").Not.Nullable();
        }
    }

    /// <summary>A two-column custom type; only the members SessionFactory construction touches do anything.</summary>
    public class GoldenFlagsUserType : IUserType
    {
        public new bool Equals(object x, object y) => ReferenceEquals(x, y);
        public int GetHashCode(object x) => x?.GetHashCode() ?? 0;
        public object NullSafeGet(DbDataReader rs, string[] names, ISessionImplementor session, object owner) => throw new NotSupportedException();
        public void NullSafeSet(DbCommand cmd, object value, int index, ISessionImplementor session) => throw new NotSupportedException();
        public object DeepCopy(object value) => value;
        public object Replace(object original, object target, object owner) => original;
        public object Assemble(object cached, object owner) => cached;
        public object Disassemble(object value) => value;
        public Type ReturnedType => typeof(bool[]);
        public bool IsMutable => false;
        public SqlType[] SqlTypes => [NHibernateUtil.Boolean.SqlType, NHibernateUtil.Boolean.SqlType];
    }

    class GoldenLineMap : ClassMap<GoldenLine>
    {
        public GoldenLineMap()
        {
            CompositeId().KeyProperty(x => x.OrderId).KeyProperty(x => x.LineNo);
            Map(x => x.Amount).Precision(18).Scale(4);
        }
    }
}
