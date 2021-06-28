﻿// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Microsoft.Extensions.FileSystemGlobbing;

namespace Microsoft.AspNetCore.Razor.Tasks
{
    // Define static web asset builds a normalized static web asset out of a set of candidate assets.
    // The BaseAssets might already define all the properties the assets care about and in that case,
    // those properties are preserved and reused. If some other properties are not present, default
    // values are used when possible. Otherwise, the properties need to be specified explicitly on the task or
    // else an error is generated. The property overrides allows the caller to override the existing value for a property
    // with the provided value.
    // There is an asset pattern filter that can be used to apply the pattern to a subset of the candidate assets
    // which allows for applying a different set of values to a subset of the candidates without having to previously
    // filter them. The asset pattern filter is applied after we've determined the RelativePath for the candidate asset.
    // There is also a RelativePathPattern that is used to automatically tranform the relative path of the candidates to match
    // the expected path of the final asset. This is typically use to remove a common path prefix, like `wwwroot` from the target
    // path of the assets and so on.
    public class DefineStaticWebAssets : Task
    {
        [Required]
        public ITaskItem [] CandidateAssets { get; set; }

        public ITaskItem [] PropertyOverrides { get; set; }

        public string SourceId { get; set; }

        public string SourceType { get; set; }

        public string BasePath { get; set; }

        public string ContentRoot { get; set; }

        public string RelativePathPattern { get; set; }

        public string RelativePathFilter { get; set; }

        public string AssetKind { get; set; } = StaticWebAsset.AssetKinds.All;

        public string AssetMode { get; set; } = StaticWebAsset.AssetModes.All;

        public string CopyToOutputDirectory { get; set; } = StaticWebAsset.AssetCopyOptions.Never;

        public string CopyToPublishDirectory { get; set; } = StaticWebAsset.AssetCopyOptions.PreserveNewest;

        [Output]
        public ITaskItem[] Assets { get; set; }

        public override bool Execute()
        {
            try
            {
                var results = new List<ITaskItem>();
                var matcher = !string.IsNullOrEmpty(RelativePathPattern) ? new Matcher().AddInclude(RelativePathPattern) : null;
                var filter = !string.IsNullOrEmpty(RelativePathFilter) ? new Matcher().AddInclude(RelativePathFilter) : null;
                for (var i = 0; i < CandidateAssets.Length; i++)
                {
                    var candidate = CandidateAssets[i];
                    var relativePathCandidate = GetCandidateMatchPath(candidate);
                    if (matcher != null)
                    {
                        var match = matcher.Match(relativePathCandidate);
                        if (match.HasMatches)
                        {
                            var newRelativePathCandidate = match.Files.Single().Stem;
                            Log.LogMessage(
                                "The relative path '{0}' matched the pattern '{1}'. Replacing relative path with '{2}'.",
                                relativePathCandidate,
                                RelativePathPattern,
                                newRelativePathCandidate);

                            relativePathCandidate = newRelativePathCandidate;
                        }
                    }

                    if (filter != null && !filter.Match(relativePathCandidate).HasMatches)
                    {
                        Log.LogMessage(
                            "Skipping '{0}' becasue the relative path '{1}' did not match the filter '{2}'.",
                            candidate.ItemSpec,
                            relativePathCandidate,
                            RelativePathPattern);

                        continue;
                    }
                    var sourceId = ComputePropertyValue(candidate, nameof(StaticWebAsset.SourceId), SourceId);
                    var sourceType = ComputePropertyValue(candidate, nameof(StaticWebAsset.SourceType), SourceType);
                    var basePath = ComputePropertyValue(candidate, nameof(StaticWebAsset.BasePath), BasePath);
                    var contentRoot = ComputePropertyValue(candidate, nameof(StaticWebAsset.ContentRoot), ContentRoot);
                    var assetKind = ComputePropertyValue(candidate, nameof(StaticWebAsset.AssetKind), AssetKind);
                    var assetMode = ComputePropertyValue(candidate, nameof(StaticWebAsset.AssetMode), AssetMode);
                    var copyToOutputDirectory = ComputePropertyValue(candidate, nameof(StaticWebAsset.CopyToOutputDirectory), CopyToOutputDirectory);
                    var copyToPublishDirectory = ComputePropertyValue(candidate, nameof(StaticWebAsset.CopyToPublishDirectory), CopyToPublishDirectory);
                    
                    // If we are not able to compute the value based on an existing value or a default, we produce an error and stop.
                    if (Log.HasLoggedErrors)
                    {
                        break;
                    }

                    var asset = StaticWebAsset.FromProperties(
                        candidate.GetMetadata("FullPath"),
                        sourceId,
                        sourceType,
                        basePath,
                        relativePathCandidate,
                        contentRoot,
                        assetKind,
                        assetMode,
                        copyToOutputDirectory,
                        copyToPublishDirectory);

                    var item = asset.ToTaskItem();
                    item.SetMetadata("OriginalItemSpec", item.ItemSpec);

                    results.Add(item);
                }

                Assets = results.ToArray();
            }
            catch (Exception ex)
            {
                Log.LogError(ex.Message);
            }

            return !Log.HasLoggedErrors;
        }

        private string ComputePropertyValue(ITaskItem element, string metadataName, string propertyValue)
        {
            if (PropertyOverrides != null && PropertyOverrides.Any(a => string.Equals(a.ItemSpec, metadataName, StringComparison.OrdinalIgnoreCase)))
            {
                return propertyValue;
            }

            var value = element.GetMetadata(metadataName);
            if (string.IsNullOrEmpty(value))
            {
                if (propertyValue == null)
                {
                    Log.LogError("No metadata '{0}' was present for item '{1}' and no default value was provided.",
                        metadataName,
                        element.ItemSpec);
                    
                    return null;
                }
                else
                {
                    return propertyValue;
                }
            }
            else
            {
                return value;
            }
        }

        private string GetCandidateMatchPath(ITaskItem candidate)
        {
            var targetPath = candidate.GetMetadata("TargetPath");
            if (!string.IsNullOrEmpty(targetPath))
            {
                Log.LogMessage("TargetPath '{0}' found for candidate '{1}' and will be used for matching.", targetPath, candidate.ItemSpec);
                return targetPath;
            }

            var relativePath = candidate.GetMetadata("RelativePath");
            if (!string.IsNullOrEmpty(relativePath))
            {
                Log.LogMessage("RelativePath '{0}' found for candidate '{1}' and will be used for matching.", relativePath, candidate.ItemSpec);

                return relativePath;
            }

            var linkPath = candidate.GetMetadata("Link");
            if (!string.IsNullOrEmpty(linkPath))
            {
                Log.LogMessage("Link '{0}' found for candidate '{1}' and will be used for matching.", linkPath, candidate.ItemSpec);

                return linkPath;
            }

            var normalizedContentRoot = Path.GetFullPath(candidate.GetMetadata(nameof(StaticWebAsset.ContentRoot)) ?? ContentRoot)
                .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            
            if (!normalizedContentRoot.EndsWith(Path.DirectorySeparatorChar.ToString()))
            {
                normalizedContentRoot += Path.DirectorySeparatorChar;
            }

            var normalizedAssetPath = Path.GetFullPath(candidate.GetMetadata("FullPath"));

            if (normalizedAssetPath.StartsWith(normalizedContentRoot))
            {
                return normalizedAssetPath.Substring(normalizedContentRoot.Length);
            }
            else
            {
                return candidate.ItemSpec;
            }
        }
    }
}