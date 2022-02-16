﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using NuGet.Packaging.Core;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Microsoft.DotNet.SourceBuild.Tasks.UsageReport
{
    public class ValidateUsageAgainstBaseline : Task
    {
        [Required]
        public string DataFile { get; set; }

        [Required]
        public string BaselineDataFile { get; set; }

        [Required]
        public string OutputBaselineFile { get; set; }

        [Required]
        public string OutputReportFile { get; set; }

        public bool AllowTestProjectUsage { get; set; }

        public override bool Execute()
        {
            var used = UsageData.Parse(XElement.Parse(File.ReadAllText(DataFile)));

            string baselineText = File.ReadAllText(BaselineDataFile);

            var baseline = UsageData.Parse(XElement.Parse(baselineText));

            UsageValidationData data = GetUsageValidationData(baseline, used);

            Directory.CreateDirectory(Path.GetDirectoryName(OutputBaselineFile));
            File.WriteAllText(OutputBaselineFile, data.ActualUsageData.ToXml().ToString());

            Directory.CreateDirectory(Path.GetDirectoryName(OutputReportFile));
            File.WriteAllText(OutputReportFile, data.Report.ToString());

            return !Log.HasLoggedErrors;
        }

        public UsageValidationData GetUsageValidationData(UsageData baseline, UsageData used)
        {
            // Remove prebuilts from the used data if the baseline says to ignore them. Do this
            // first, so the new generated baseline doesn't list usages that are ignored by a
            // pattern anyway.
            ApplyBaselineIgnorePatterns(used, baseline);

            // Find new, removed, and unchanged usage after filtering patterns.
            Comparison<PackageIdentity> diff = Compare(
                used.Usages.Select(u => u.GetIdentityWithoutRid()).Distinct(),
                baseline.Usages.Select(u => u.GetIdentityWithoutRid()).Distinct());

            var report = new XElement("BaselineComparison");

            bool tellUserToUpdateBaseline = false;

            if (diff.Added.Any())
            {
                tellUserToUpdateBaseline = true;

                string BaselineErrorMessage = $"{diff.Added.Length} new packages used not in baseline! See report " +
                    $"at {OutputReportFile} for more information. Package IDs are:\n" +
                    string.Join("\n", diff.Added.Select(u => u.ToString()));

                Log.LogError(BaselineErrorMessage);

                Log.LogMessage(
                    MessageImportance.High,
                    "##vso[task.complete result=SucceededWithIssues;]" + BaselineErrorMessage);

                // In the report, list full usage info, not only identity.
                report.Add(
                    new XElement(
                        "New",
                        used.Usages
                            .Where(u => diff.Added.Contains(u.GetIdentityWithoutRid()))
                            .Select(u => u.ToXml())));
            }

            if (diff.Removed.Any())
            {
                tellUserToUpdateBaseline = true;
                Log.LogMessage(
                    MessageImportance.High,
                    $"{diff.Removed.Length} packages in baseline weren't used!");

                report.Add(new XElement("Removed", diff.Removed.Select(id => id.ToXElement())));
            }

            if (diff.Unchanged.Any())
            {
                Log.LogMessage(
                    MessageImportance.High,
                    $"{diff.Unchanged.Length} packages used as expected in the baseline.");
            }

            if (!AllowTestProjectUsage)
            {
                Usage[] testProjectUsages = used.Usages
                    .Where(WriteUsageReports.IsTestUsageByHeuristic)
                    .ToArray();

                if (testProjectUsages.Any())
                {
                    string[] projects = testProjectUsages
                        .Select(u => u.AssetsFile)
                        .Distinct()
                        .ToArray();

                    Log.LogError(
                        $"{testProjectUsages.Length} forbidden test usages found in " +
                        $"{projects.Length} projects:\n" +
                        string.Join("\n", projects));
                }
            }

            // Simplify the used data to what is necessary for a baseline, to reduce file size.
            foreach (var usage in used.Usages)
            {
                usage.AssetsFile = null;
            }

            used.ProjectDirectories = null;
            used.Usages = used.Usages.Distinct().ToArray();

            if (tellUserToUpdateBaseline)
            {
                Log.LogWarning(
                    "Prebuilt usages are different from the baseline found at " +
                    $"'{BaselineDataFile}'. If it's acceptable to update the baseline, copy the " +
                    $"contents of the automatically generated baseline '{OutputBaselineFile}'.");
            }

            return new UsageValidationData
            {
                Report = report,
                ActualUsageData = used
            };
        }

        private static void ApplyBaselineIgnorePatterns(UsageData actual, UsageData baseline)
        {
            Regex[] ignoreUsageRegexes = baseline.IgnorePatterns.NullAsEmpty()
                .Select(p => p.CreateRegex())
                .ToArray();

            actual.IgnorePatterns = baseline.IgnorePatterns;

            var ignoredUsages = actual.Usages
                .Where(usage =>
                {
                    string id = $"{usage.PackageIdentity.Id}/{usage.PackageIdentity.Version}";
                    return ignoreUsageRegexes.Any(r => r.IsMatch(id));
                })
                .ToArray();

            actual.Usages = actual.Usages.Except(ignoredUsages).ToArray();
        }

        private static Comparison<T> Compare<T>(IEnumerable<T> actual, IEnumerable<T> baseline)
        {
            return new Comparison<T>(actual.ToArray(), baseline.ToArray());
        }

        private class Comparison<T>
        {
            public T[] Added { get; }
            public T[] Removed { get; }
            public T[] Unchanged { get; }

            public Comparison(
                IEnumerable<T> actual,
                IEnumerable<T> baseline)
            {
                Added = actual.Except(baseline).ToArray();
                Removed = baseline.Except(actual).ToArray();
                Unchanged = actual.Intersect(baseline).ToArray();
            }
        }
    }
}
