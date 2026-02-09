using System;
using FluentNHibernate.Diagnostics;
using NUnit.Framework;

namespace FluentNHibernate.Testing.Diagnostics;

[TestFixture]
public class DefaultOutputFormatterTests
{
    [Test]
    public void should_produce_simple_format()
    {
        var formatter = new DefaultOutputFormatter();
        var results = new DiagnosticResults(new[]
            {
                new ScannedSource
                {
                    Identifier = typeof(One).Assembly.GetName().FullName,
                    Phase = ScanPhase.FluentMappings
                },
                new ScannedSource
                {
                    Identifier = typeof(One).Assembly.GetName().FullName,
                    Phase = ScanPhase.Conventions
                }
            },
            new[] { typeof(Two), typeof(One) },
            new[] { typeof(Two), typeof(One) },
            new[]
            {
                new SkippedAutomappingType
                {
                    Type = typeof(One),
                    Reason = "first reason"
                },
                new SkippedAutomappingType
                {
                    Type = typeof(Two),
                    Reason = "second reason"
                },
            },
            new[] { typeof(Two), typeof(One) },
            new[]
            {
                new AutomappingType
                {
                    Type = typeof(One)
                },
                new AutomappingType
                {
                    Type = typeof(Two)
                },
            });
        var output = formatter.Format(results);
        var nl = Environment.NewLine;

        output.ShouldEqual(
            $"Fluent Mappings{nl}" +
            $"---------------{nl}{nl}" +
            $"Sources scanned:{nl}{nl}" +
            "  " + typeof(One).Assembly.GetName().FullName + nl +
            nl +
            $"Mappings discovered:{nl}{nl}" +
            "  " + typeof(One).Name + " | " + typeof(One).AssemblyQualifiedName + nl +
            "  " + typeof(Two).Name + " | " + typeof(Two).AssemblyQualifiedName + nl +
            nl +
            $"Conventions{nl}" +
            $"-----------{nl}{nl}" +
            $"Sources scanned:{nl}{nl}" +
            "  " + typeof(One).Assembly.GetName().FullName + nl +
            nl +
            $"Conventions discovered:{nl}{nl}" +
            "  " + typeof(One).Name + " | " + typeof(One).AssemblyQualifiedName + nl +
            "  " + typeof(Two).Name + " | " + typeof(Two).AssemblyQualifiedName + nl +
            nl +
            $"Automapping{nl}" +
            $"-----------{nl}{nl}" +
            $"Skipped types:{nl}{nl}" + 
            "  " + typeof(One).Name + " | first reason  | " + typeof(One).AssemblyQualifiedName + nl +
            "  " + typeof(Two).Name + " | second reason | " + typeof(Two).AssemblyQualifiedName + nl +
            nl +
            $"Candidate types:{nl}{nl}" +
            "  " + typeof(One).Name + " | " + typeof(One).AssemblyQualifiedName + nl +
            "  " + typeof(Two).Name + " | " + typeof(Two).AssemblyQualifiedName + nl +
            nl + 
            $"Mapped types:{nl}{nl}" +
            "  " + typeof(One).Name + " | " + typeof(One).AssemblyQualifiedName + nl +
            "  " + typeof(Two).Name + " | " + typeof(Two).AssemblyQualifiedName + nl
        );
    }

    class One { }
    class Two { }
}
