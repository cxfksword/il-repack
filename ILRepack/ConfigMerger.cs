//
// Copyright (c) 2011 Francois Valdy
//
//   Licensed under the Apache License, Version 2.0 (the "License");
//   you may not use this file except in compliance with the License.
//   You may obtain a copy of the License at
//
//       http://www.apache.org/licenses/LICENSE-2.0
//
//   Unless required by applicable law or agreed to in writing, software
//   distributed under the License is distributed on an "AS IS" BASIS,
//   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//   See the License for the specific language governing permissions and
//   limitations under the License.
//

using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Xml;

namespace ILRepacking
{
    internal static class ConfigMerger
    {
        internal static void Process(ILRepack repack)
        {
            try
            {
                var validConfigFiles = new List<string>();
                foreach (string assembly in repack.MergedAssemblyFiles)
                {
                    string assemblyConfig = assembly + ".config";
                    if (!File.Exists(assemblyConfig))
                        continue;
                    var doc = new XmlDocument();
                    doc.Load(assemblyConfig);
                    validConfigFiles.Add(assemblyConfig);
                }

                if (validConfigFiles.Count == 0)
                    return;

                string firstFile = validConfigFiles[0];
                var dataset = new DataSet();
                dataset.ReadXml(firstFile);

                var mergedAssemblies = new HashSet<string>(repack.MergedAssemblies.Select(a => a.Name.Name));

                ProcessRedirects(dataset, mergedAssemblies, repack.Options);

                foreach (string configFile in validConfigFiles.Skip(1))
                {
                    var nextDataset = new DataSet();
                    nextDataset.ReadXml(configFile);
                    ProcessRedirects(nextDataset, mergedAssemblies, repack.Options);
                    RemoveVersions(nextDataset);
                    dataset.Merge(nextDataset);
                }
                dataset.WriteXml(repack.Options.OutputFile + ".config");
            }
            catch (Exception e)
            {
                repack.Logger.Error("Failed to merge configuration files: " + e);
            }
        }

        private static void RemoveVersions(DataSet set)
        {
            var table = set.Tables["supportedRuntime"];
            if (table == null) return;

            var versions = table.Select().ToList();
            foreach (var row in versions)
            {
                row.Delete();
            }
            table.AcceptChanges();
        }

        private static void ProcessRedirects(DataSet set, HashSet<string> mergedAssemblies, RepackOptions repackOptions)
        {
            if(repackOptions.KeepOtherVersionReferences) return;

            var table = set.Tables["assemblyIdentity"];
            var table2 = set.Tables["dependentAssembly"];
            if (table == null || table2 == null) return;
            var parentRelation = table.ParentRelations[0];
            var parentRelation2 = table2.ParentRelations[0];
            var nameCol = table.Columns["name"];
            var toDelete = table.Select().Where(r => mergedAssemblies.Contains(r[nameCol] as string)).ToList();
            foreach (var row in toDelete)
            {
                if(row.RowState == DataRowState.Detached) continue;
                var parent = row.GetParentRow(parentRelation);
                var parent2 = parent.GetParentRow(parentRelation2);
                parent2.Delete();
            }
            set.AcceptChanges();
        }
    }
}
