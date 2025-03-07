﻿//
// Copyright (c) 2011 Francois Valdy
// Copyright (c) 2018 Alexander Vostres
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
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using ILRepacking.Steps;
using Mono.Cecil;
using Mono.Cecil.PE;
using Mono.Unix.Native;
using ILRepacking.Mixins;
using System.Runtime.Serialization.Formatters.Binary;
using System.Diagnostics;
using ILRepacking.Steps.SourceServerData;
using ILRepacking.Steps.Win32Resources;
using Mono.Cecil.Cil;

namespace ILRepacking
{
    public class ILRepack : IRepackContext, IDisposable
    {
        internal RepackOptions Options;
        internal ILogger Logger;

        internal IList<string> MergedAssemblyFiles { get; set; }
        internal string PrimaryAssemblyFile { get; set; }
        // contains all 'other' assemblies, but not the primary assembly
        public IList<AssemblyDefinition> OtherAssemblies { get; private set; }
        // contains all assemblies, primary (first one) and 'other'
        public IList<AssemblyDefinition> MergedAssemblies { get; private set; }
        public AssemblyDefinition TargetAssemblyDefinition { get; private set; }
        public AssemblyDefinition PrimaryAssemblyDefinition { get; private set; }
        public RepackAssemblyResolver GlobalAssemblyResolver { get; } = new RepackAssemblyResolver();

        public ModuleDefinition TargetAssemblyMainModule => TargetAssemblyDefinition.MainModule;
        public ModuleDefinition PrimaryAssemblyMainModule => PrimaryAssemblyDefinition.MainModule;

        private IKVMLineIndexer _lineIndexer;
        private ReflectionHelper _reflectionHelper;
        private PlatformFixer _platformFixer;
        private MappingHandler _mappingHandler;

        private static readonly Regex TypeRegex = new Regex("^(.*?), ([^>,]+), .*$");

        IKVMLineIndexer IRepackContext.LineIndexer => _lineIndexer;
        ReflectionHelper IRepackContext.ReflectionHelper => _reflectionHelper;
        PlatformFixer IRepackContext.PlatformFixer => _platformFixer;
        MappingHandler IRepackContext.MappingHandler => _mappingHandler;
        private readonly Dictionary<AssemblyDefinition, int> _aspOffsets = new Dictionary<AssemblyDefinition, int>();

        private readonly RepackImporter _repackImporter;

        public ILRepack(RepackOptions options)
            : this(options, new RepackLogger(options))
        {
        }

        public ILRepack(RepackOptions options, ILogger logger)
        {
            Options = options;
            Logger = logger;

            _repackImporter = new RepackImporter(Logger, Options, this, _aspOffsets);
        }

        private void ReadInputAssemblies()
        {
            MergedAssemblyFiles = Options.ResolveFiles();
            OtherAssemblies = new List<AssemblyDefinition>();
            // TODO: this could be parallelized to gain speed
            var primary = MergedAssemblyFiles.FirstOrDefault();
            var debugSymbolsRead = false;
            PrepareAssemblyResolver(primary);

            foreach (string assembly in MergedAssemblyFiles)
            {
                var result = ReadInputAssembly(assembly, primary == assembly);
                if (result.IsPrimary)
                {
                    PrimaryAssemblyDefinition = result.Definition;
                    PrimaryAssemblyFile = result.Assembly;
                }
                else
                    OtherAssemblies.Add(result.Definition);

                debugSymbolsRead |= result.SymbolsRead;
            }
            // prevent writing PDB if we haven't read any
            Options.DebugInfo = debugSymbolsRead;

            MergedAssemblies = new List<AssemblyDefinition>(OtherAssemblies);
            MergedAssemblies.Insert(0, PrimaryAssemblyDefinition);
        }

        private void PrepareAssemblyResolver(string primaryAssembly)
        {
            ReaderParameters rp = new ReaderParameters(ReadingMode.Deferred);
            rp.ThrowIfSymbolsAreNotMatching = false;
            using var asm = AssemblyDefinition.ReadAssembly(primaryAssembly, rp);
            GlobalAssemblyResolver.MatchTarget(asm.MainModule);
        }

        private AssemblyDefinitionContainer ReadInputAssembly(string assembly, bool isPrimary)
        {
            Logger.Info("Adding assembly for merge: " + assembly);
            try
            {
                ReaderParameters rp = new ReaderParameters(ReadingMode.Immediate) { AssemblyResolver = GlobalAssemblyResolver };

                if (Options.DebugInfo)
                {
                    rp.ReadSymbols = true;
                    rp.SymbolReaderProvider = new DefaultSymbolReaderProvider(false);
                }
                AssemblyDefinition mergeAsm;
                try
                {
                    mergeAsm = AssemblyDefinition.ReadAssembly(assembly, rp);
                }
                catch (BadImageFormatException e) when (!rp.ReadSymbols)
                {
                    throw new InvalidOperationException(
                        "ILRepack does not support merging non-.NET libraries (e.g.: native libraries)", e);
                }
                // cope with invalid symbol file
                catch (Exception) when (rp.ReadSymbols)
                {
                    rp.ReadSymbols = false;
                    try
                    {
                        mergeAsm = AssemblyDefinition.ReadAssembly(assembly, rp);
                    }
                    catch (BadImageFormatException e)
                    {
                        throw new InvalidOperationException(
                            "ILRepack does not support merging non-.NET libraries (e.g.: native libraries)", e);
                    }
                    Logger.Info("Failed to load debug information for " + assembly);
                }

                if (!Options.AllowZeroPeKind && (mergeAsm.MainModule.Attributes & ModuleAttributes.ILOnly) == 0)
                    throw new ArgumentException("Failed to load assembly with Zero PeKind: " + assembly);
                GlobalAssemblyResolver.RegisterAssembly(mergeAsm);

                return new AssemblyDefinitionContainer
                {
                    Assembly = assembly,
                    Definition = mergeAsm,
                    IsPrimary = isPrimary,
                    SymbolsRead = rp.ReadSymbols
                };
            }
            catch
            {
                Logger.Error("Failed to load assembly " + assembly);
                throw;
            }
        }

        IMetadataScope IRepackContext.MergeScope(IMetadataScope scope)
        {
            if (scope is AssemblyNameReference)
                return TargetAssemblyMainModule.AssemblyReferences.AddUniquely((Mono.Cecil.AssemblyNameReference)scope);
            Logger.Warn("Merging a module scope, probably not supported");
            return scope;
        }

        internal class AssemblyDefinitionContainer
        {
            public bool SymbolsRead { get; set; }
            public AssemblyDefinition Definition { get; set; }
            public string Assembly { get; set; }
            public bool IsPrimary { get; set; }
        }

        public enum Kind
        {
            Dll,
            Exe,
            WinExe,
            SameAsPrimaryAssembly
        }


        private TargetRuntime ParseTargetPlatform()
        {
            TargetRuntime runtime = PrimaryAssemblyMainModule.Runtime;
            if (Options.TargetPlatformVersion != null)
            {
                switch (Options.TargetPlatformVersion)
                {
                    case "v2": runtime = TargetRuntime.Net_2_0; break;
                    case "v4": runtime = TargetRuntime.Net_4_0; break;
                    default: throw new ArgumentException($"Invalid TargetPlatformVersion: '{Options.TargetPlatformVersion}'");
                }
                _platformFixer.ParseTargetPlatformDirectory(runtime, Options.TargetPlatformDirectory);
            }
            return runtime;
        }

        private string ResolveTargetPlatformDirectory(string version)
        {
            if (version == null)
                return null;
            var platformBasePath = Path.GetDirectoryName(Path.GetDirectoryName(typeof(string).Assembly.Location));
            List<string> platformDirectories = new List<string>(Directory.GetDirectories(platformBasePath));
            var platformDir = version.Substring(1);
            if (platformDir.Length == 1) platformDir = platformDir + ".0";
            // mono platform dir is '2.0' while windows is 'v2.0.50727'
            var targetPlatformDirectory = platformDirectories
                .FirstOrDefault(x => Path.GetFileName(x).StartsWith(platformDir) || Path.GetFileName(x).StartsWith($"v{platformDir}"));
            if (targetPlatformDirectory == null)
                throw new ArgumentException($"Failed to find target platform '{Options.TargetPlatformVersion}' in '{platformBasePath}'");
            Logger.Info($"Target platform directory resolved to {targetPlatformDirectory}");
            return targetPlatformDirectory;
        }

        public static IEnumerable<AssemblyName> GetRepackAssemblyNames(Type typeInRepackedAssembly)
        {
            try
            {
                using (Stream stream = typeInRepackedAssembly.Assembly.GetManifestResourceStream(ResourcesRepackStep.ILRepackListResourceName))
                    if (stream != null)
                    {
                        string[] list = (string[])new BinaryFormatter().Deserialize(stream);
                        return list.Select(x => new AssemblyName(x));
                    }
            }
            catch (Exception)
            {
            }
            return Enumerable.Empty<AssemblyName>();
        }

        public static AssemblyName GetRepackAssemblyName(IEnumerable<AssemblyName> repackAssemblyNames, string repackedAssemblyName, Type fallbackType)
        {
            return repackAssemblyNames?.FirstOrDefault(name => name.Name == repackedAssemblyName) ?? fallbackType.Assembly.GetName();
        }

        void PrintRepackHeader()
        {
            var assemblies = GetRepackAssemblyNames(typeof(ILRepack));
            var ilRepack = GetRepackAssemblyName(assemblies, "ILRepack", typeof(ILRepack));
            Logger.Info($"IL Repack - Version {ilRepack.Version.ToString(3)}");
            Logger.Verbose($"Runtime: {typeof(ILRepack).Assembly.FullName}");
            Logger.Info(Options.ToCommandLine());
        }

        /// <summary>
        /// The actual repacking process, called by main after parsing arguments.
        /// When referencing this assembly, call this after setting the merge properties.
        /// </summary>
        public void Repack()
        {
            var timer = new Stopwatch();
            timer.Start();
            Options.Validate();
            PrintRepackHeader();

            var actualOutFile = Options.OutputFile;
            Options.OutputFile = GetTempFile(Options.OutputFile);
            _reflectionHelper = new ReflectionHelper(this);
            ResolveSearchDirectories();

            // Read input assemblies only after all properties are set.
            ReadInputAssemblies();

            if (!Options.KeepOtherVersionReferences)
            {
                _platformFixer = new PlatformAndDuplicateFixer(this, PrimaryAssemblyMainModule.Runtime);
            }
            else
            {
                _platformFixer = new PlatformFixer(this, PrimaryAssemblyMainModule.Runtime);
            }

            _mappingHandler = new MappingHandler();
            bool hadStrongName = PrimaryAssemblyDefinition.Name.HasPublicKey;

            ModuleKind kind = PrimaryAssemblyMainModule.Kind;
            if (Options.TargetKind.HasValue)
            {
                switch (Options.TargetKind.Value)
                {
                    case Kind.Dll: kind = ModuleKind.Dll; break;
                    case Kind.Exe: kind = ModuleKind.Console; break;
                    case Kind.WinExe: kind = ModuleKind.Windows; break;
                }
            }
            TargetRuntime runtime = ParseTargetPlatform();

            // change assembly's name to correspond to the file we create
            string mainModuleName = Path.GetFileNameWithoutExtension(Options.OutputFile);

            if (TargetAssemblyDefinition == null)
            {
                AssemblyNameDefinition asmName = Clone(PrimaryAssemblyDefinition.Name);
                asmName.Name = mainModuleName;
                TargetAssemblyDefinition = AssemblyDefinition.CreateAssembly(asmName, mainModuleName,
                    new ModuleParameters()
                    {
                        Kind = kind,
                        Architecture = PrimaryAssemblyMainModule.Architecture,
                        AssemblyResolver = GlobalAssemblyResolver,
                        Runtime = runtime
                    });
            }
            else
            {
                // TODO: does this work or is there more to do?
                TargetAssemblyMainModule.Kind = kind;
                TargetAssemblyMainModule.Runtime = runtime;

                TargetAssemblyDefinition.Name.Name = mainModuleName;
                TargetAssemblyMainModule.Name = mainModuleName;
            }
            // set the main module attributes
            TargetAssemblyMainModule.Attributes = PrimaryAssemblyMainModule.Attributes;
            var win32ResourceStep = new Win32ResourceStep(Logger, this, _aspOffsets);

            if (Options.Version != null)
                TargetAssemblyDefinition.Name.Version = Options.Version;

            _lineIndexer = new IKVMLineIndexer(this, Options.LineIndexation);
            var signingStep = new SigningStep(this, Options);
            var isUnixEnvironment = Environment.OSVersion.Platform == PlatformID.MacOSX || Environment.OSVersion.Platform == PlatformID.Unix;

            using (var sourceServerDataStep = GetSourceServerDataStep(isUnixEnvironment))
            {
                List<IRepackStep> repackSteps = new List<IRepackStep>
                {
                    win32ResourceStep,
                    signingStep,
                    new ReferencesRepackStep(Logger, this, Options),
                    new TypesRepackStep(Logger, this, _repackImporter, Options),
                    new ResourcesRepackStep(Logger, this, Options),
                    new AttributesRepackStep(Logger, this, _repackImporter, Options),
                    new ReferencesFixStep(Logger, this, _repackImporter, Options),
                    new PublicTypesFixStep(Logger, this, _repackImporter, Options),
                    new XamlResourcePathPatcherStep(Logger, this),
                    sourceServerDataStep
                };

                foreach (var step in repackSteps)
                {
                    step.Perform();
                }
                
                var parameters = new WriterParameters
                {
                    StrongNameKeyPair = signingStep.KeyPair,
                    WriteSymbols = Options.DebugInfo && PrimaryAssemblyMainModule.SymbolReader != null,
                    SymbolWriterProvider = PrimaryAssemblyMainModule.SymbolReader?.GetWriterProvider(),
                };
                // create output directory if it does not exist
                var outputDir = Path.GetDirectoryName(Options.OutputFile);
                if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir))
                {
                    Logger.Info("Output directory does not exist. Creating output directory: " + outputDir);
                    Directory.CreateDirectory(outputDir);
                }

                Logger.Info("Writing output assembly to disk");
                TargetAssemblyDefinition.Write(Options.OutputFile, parameters);
                
                sourceServerDataStep.Write();

                foreach (var assembly in MergedAssemblies)
                {
                    assembly.Dispose();
                }
                
                TargetAssemblyDefinition.Dispose();
                GlobalAssemblyResolver.Dispose();

                win32ResourceStep.Patch(Options.OutputFile);

                MoveTempFile(Options.OutputFile, actualOutFile);
                Options.OutputFile = actualOutFile;

                // If this is an executable and we are on linux/osx we should copy file permissions from
                // the primary assembly
                if (isUnixEnvironment && (kind == ModuleKind.Console || kind == ModuleKind.Windows))
                {
                    Stat stat;
                    Logger.Info("Copying permissions from " + PrimaryAssemblyFile);
                    Syscall.stat(PrimaryAssemblyFile, out stat);
                    Syscall.chmod(Options.OutputFile, stat.st_mode);
                }
                if (hadStrongName && !TargetAssemblyDefinition.Name.HasPublicKey)
                    Options.StrongNameLost = true;

                // nice to have, merge .config (assembly configuration file) & .xml (assembly documentation)
                ConfigMerger.Process(this);
                if (Options.XmlDocumentation)
                    DocumentationMerger.Process(this);
            }

            Logger.Info($"Finished in {timer.Elapsed}");
        }

        private void MoveTempFile(string tempFile, string outFile)
        {
            var srcDir = Path.GetDirectoryName(tempFile);
            var tgtDir = Path.GetDirectoryName(outFile);
            if (!string.IsNullOrEmpty(tgtDir))
            {
                Directory.CreateDirectory(tgtDir);
            }

            foreach (var srcFileName in Directory.EnumerateFiles(srcDir))
            {
                var fileName = Path.GetFileName(srcFileName);
                var tgtFileName = Path.Combine(tgtDir, fileName);
                if (File.Exists(tgtFileName))
                {
                    File.Delete(tgtFileName);
                }
                File.Move(srcFileName, tgtFileName);
            }

            Directory.Delete(srcDir, false);
        }

        private string GetTempFile(string outputFileName)
        {
            var fileName = Path.GetFileName(outputFileName);
            var dirName = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(dirName);
            return Path.Combine(dirName, fileName);
        }

        private ISourceServerDataRepackStep GetSourceServerDataStep(bool isUnixEnvironment)
        {
            if (isUnixEnvironment)
            {
                return new NullSourceServerStep(Logger);
            }
            else
            {
                return new SourceServerDataRepackStep(Options.OutputFile, MergedAssemblyFiles);
            }
        }

        private void ResolveSearchDirectories()
        {
            foreach (var dir in Options.SearchDirectories)
                GlobalAssemblyResolver.AddSearchDirectory(dir);
            var targetPlatformDirectory = Options.TargetPlatformDirectory ?? ResolveTargetPlatformDirectory(Options.TargetPlatformVersion);
            if (targetPlatformDirectory != null)
            {
                GlobalAssemblyResolver.AddSearchDirectory(targetPlatformDirectory);
                var facadesDirectory = Path.Combine(targetPlatformDirectory, "Facades");
                if (Directory.Exists(facadesDirectory))
                    GlobalAssemblyResolver.AddSearchDirectory(facadesDirectory);
            }
        }

        string IRepackContext.FixStr(string content)
        {
            return FixStr(content, false);
        }

        string IRepackContext.FixReferenceInIkvmAttribute(string content)
        {
            return FixStr(content, true);
        }

        private string FixStr(string content, bool javaAttribute)
        {
            if (String.IsNullOrEmpty(content) || content.Length > 512 || content.IndexOf(", ") == -1 || content.StartsWith("System."))
                return content;
            // TODO fix "TYPE, ASSEMBLYNAME, CULTURE" pattern
            // TODO fix "TYPE, ASSEMBLYNAME, VERSION, CULTURE, TOKEN" pattern
            var match = TypeRegex.Match(content);
            if (match.Success)
            {
                string type = match.Groups[1].Value;
                string targetAssemblyName = TargetAssemblyDefinition.FullName;
                if (javaAttribute)
                    targetAssemblyName = targetAssemblyName.Replace('.', '/') + ";";

                if (MergedAssemblies.Any(x => x.Name.Name == match.Groups[2].Value))
                {
                    return type + ", " + targetAssemblyName;
                }
            }
            return content;
        }

        string IRepackContext.FixTypeName(string assemblyName, string typeName)
        {
            // TODO handle renames
            return typeName;
        }

        string IRepackContext.FixAssemblyName(string assemblyName)
        {
            if (MergedAssemblies.Any(x => x.FullName == assemblyName))
            {
                // TODO no public key token !
                return TargetAssemblyDefinition.FullName;
            }
            return assemblyName;
        }

        private AssemblyNameDefinition Clone(AssemblyNameDefinition assemblyName)
        {
            AssemblyNameDefinition asmName = new AssemblyNameDefinition(assemblyName.Name, assemblyName.Version);
            asmName.Attributes = assemblyName.Attributes;
            asmName.Culture = assemblyName.Culture;
            asmName.Hash = assemblyName.Hash;
            asmName.HashAlgorithm = assemblyName.HashAlgorithm;
            asmName.PublicKey = assemblyName.PublicKey;
            asmName.PublicKeyToken = assemblyName.PublicKeyToken;
            return asmName;
        }

        TypeDefinition IRepackContext.GetMergedTypeFromTypeRef(TypeReference reference)
        {
            return _mappingHandler.GetRemappedType(reference);
        }

        TypeReference IRepackContext.GetExportedTypeFromTypeRef(TypeReference type)
        {
            return _mappingHandler.GetExportedRemappedType(type) ?? type;
        }

        public void Dispose()
        {
            TargetAssemblyDefinition?.Dispose();
            PrimaryAssemblyDefinition?.Dispose();
            GlobalAssemblyResolver?.Dispose();
            Logger?.Dispose();
        }
    }
}
