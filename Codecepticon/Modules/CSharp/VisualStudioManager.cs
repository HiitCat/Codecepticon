using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Reflection;
using Codecepticon.Utils;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;
using BuildEvaluation = Microsoft.Build.Evaluation;
using Newtonsoft.Json;
using Microsoft.Build.Framework;
using Microsoft.Build.Logging;
using Codecepticon.CommandLine;

namespace Codecepticon.Modules.CSharp
{
    class VisualStudioManager
    {
        private static readonly object _registerLock = new object();
        private static bool _msbuildRegistered = false;

        // Register MSBuild on demand to prevent conflicts between process-level singleton registration
        public static void EnsureMSBuildRegistered()
        {
            lock (_registerLock)
            {
                if (_msbuildRegistered) return;

                var visualStudioInstances = MSBuildLocator.QueryVisualStudioInstances().ToArray();
                if (visualStudioInstances.Length == 0)
                {
                    Logger.Warning("No MSBuild instances found to register. Install Visual Studio or Build Tools.");
                    return;
                }

                var selected = visualStudioInstances.OrderByDescending(v => v.Version).First();
                Logger.Debug($"Registering MSBuild instance: {selected.Name} v{selected.Version} at {selected.MSBuildPath}");
                MSBuildLocator.RegisterInstance(selected);
                _msbuildRegistered = true;
            }
        }

        public static MSBuildWorkspace GetWorkspace()
        {
            return GetWorkspace(new Dictionary<string, string>());
        }

        public static MSBuildWorkspace GetWorkspace(Dictionary<string, string> properties)
        {
            Logger.Verbose("Getting Visual Studio Instance");
            var instance = GetVisualStudioInstance();
            Logger.Debug($"Using MSBuild at '{instance.MSBuildPath}' - v{instance.Version} to load projects.");

            lock (_registerLock)
            {
                if (!_msbuildRegistered)
                {
                    var loaded = AppDomain.CurrentDomain.GetAssemblies()
                        .Where(a => a.GetName().Name.StartsWith("Microsoft.Build"))
                        .ToArray();
                    if (loaded.Length > 0)
                    {
                        Logger.Warning("Microsoft.Build assemblies are already loaded. If old versions (v15.x) are present, incompatibilities may occur.");
                        Logger.Warning("Loaded assemblies: " + string.Join(", ", loaded.Select(a => $"{a.GetName().Name} v{a.GetName().Version}")));
                    }
                    else
                    {
                        MSBuildLocator.RegisterInstance(instance);
                        _msbuildRegistered = true;
                    }
                }
            }

            Logger.Verbose("Creating MSBuild Workspace");

            try
            {
                return MSBuildWorkspace.Create(properties);
            }
            catch (ReflectionTypeLoadException rex)
            {
                Logger.Error("Failed to create MSBuildWorkspace due to assembly loading conflict.");
                foreach (var le in rex.LoaderExceptions)
                {
                    if (le != null)
                    {
                        Logger.Error($"LoaderException: {le.GetType().FullName}: {le.Message}");
                    }
                }

                Logger.Error("Possible causes and remediation:");
                Logger.Error("- Old Microsoft.Build v15 assemblies are loaded. Run from Visual Studio 2022 Developer Command Prompt.");
                Logger.Error("- Remove obsolete Microsoft.Build.* DLLs from executable folder or PATH.");
                Logger.Error("- Ensure Visual Studio Build Tools / Visual Studio 2019+ / 2022 is installed.");
                throw new InvalidOperationException("Cannot create MSBuildWorkspace due to incompatible Microsoft.Build dependencies. Check logs for LoaderExceptions details.", rex);
            }
            catch (TypeLoadException tex)
            {
                Logger.Error($"TypeLoadException: {tex.Message}");
                Logger.Error("Ensure no old Microsoft.Build versions exist in the executable folder or PATH.");
                throw;
            }
        }

        private static VisualStudioInstance GetVisualStudioInstance()
        {
            var visualStudioInstances = MSBuildLocator.QueryVisualStudioInstances().ToArray();
            if (visualStudioInstances.Length == 0)
            {
                throw new InvalidOperationException("No MSBuild instances found. Install Visual Studio or MSBuild tools.");
            }

            var selected = visualStudioInstances
                .OrderByDescending(v => v.Version)
                .First();

            if (visualStudioInstances.Length > 1)
            {
                Logger.Info("");
                Logger.Info("Multiple installs of MSBuild detected, selecting the latest by default:");
                Logger.Info($"\t{selected.Name} v{selected.Version}");
                Logger.Info($"\t{selected.MSBuildPath}");
                Logger.Info("");
            }

            return selected;
        }

        private static VisualStudioInstance SelectVisualStudioInstance(VisualStudioInstance[] visualStudioInstances)
        {
            Logger.Info("");
            Logger.Info("Multiple installs of MSBuild detected, please select one:");
            Logger.Info("");
            for (int i = 0; i < visualStudioInstances.Length; i++)
            {
                Logger.Info($"\t[{i + 1}]\t{visualStudioInstances[i].Name} v{visualStudioInstances[i].Version}");
                Logger.Info($"\t\t{visualStudioInstances[i].MSBuildPath}");
            }
            Logger.Info("");

            while (true)
            {
                Logger.Info("Please enter the number of your selection: ", false);
                var userResponse = Console.ReadLine();
                if (int.TryParse(userResponse, out int instanceNumber) && instanceNumber > 0 && instanceNumber <= visualStudioInstances.Length)
                {
                    return visualStudioInstances[instanceNumber - 1];
                }
                Logger.Error("Invalid choice, please try again or use Ctrl-C to quit");
            }
        }

        // Create fallback AdhocWorkspace when MSBuildWorkspace fails (e.g., missing Build Tools)
        public static Workspace CreateWorkspaceFromSolutionFile(string solutionPath)
        {
            Logger.Warning("Creating fallback AdhocWorkspace from solution file: " + solutionPath);
            var workspace = new AdhocWorkspace();
            var solutionDir = Path.GetDirectoryName(solutionPath);

            var projectPaths = new List<string>();
            foreach (var line in File.ReadAllLines(solutionPath))
            {
                if (line.StartsWith("Project(", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = line.Split(',');
                    if (parts.Length >= 2)
                    {
                        var projRel = parts[1].Trim().Trim('"');
                        var projPath = Path.GetFullPath(Path.Combine(solutionDir, projRel));
                        if (File.Exists(projPath))
                        {
                            projectPaths.Add(projPath);
                        }
                    }
                }
            }

            // Load .NET Framework 4.7.2 reference assemblies so Roslyn can resolve types
            var references = new List<MetadataReference>();
            string referenceAssembliesPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                                                          "Reference Assemblies", "Microsoft", "Framework", ".NETFramework", "v4.7.2");
            if (Directory.Exists(referenceAssembliesPath))
            {
                foreach (var dll in Directory.GetFiles(referenceAssembliesPath, "*.dll"))
                {
                    try
                    {
                        references.Add(MetadataReference.CreateFromFile(dll));
                    }
                    catch { }
                }
            }
            else
            {
                Logger.Warning("Could not find .NET Framework v4.7.2 reference assemblies at expected location: " + referenceAssembliesPath);
            }

            var solution = workspace.CurrentSolution;
            foreach (var projPath in projectPaths)
            {
                var projDir = Path.GetDirectoryName(projPath);
                var projectName = Path.GetFileNameWithoutExtension(projPath);
                var pid = ProjectId.CreateNewId();

                // Set filePath so Project.FilePath is available for build integration (needed by GetBuildProject)
                var projectInfo = ProjectInfo.Create(pid, VersionStamp.Create(), projectName, projectName, LanguageNames.CSharp, filePath: projPath)
                                     .WithMetadataReferences(references);
                solution = solution.AddProject(projectInfo);

                // Collect source files, excluding build artifacts
                var csFiles = Directory.GetFiles(projDir, "*.cs", SearchOption.AllDirectories)
                                       .Where(p => !p.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)
                                                && !p.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
                                       .ToArray();

                foreach (var file in csFiles)
                {
                    try
                    {
                        var text = SourceText.From(File.ReadAllText(file));
                        var did = DocumentId.CreateNewId(pid);
                        solution = solution.AddDocument(did, Path.GetFileName(file), text, filePath: file);
                    }
                    catch (Exception e)
                    {
                        Logger.Warning($"Could not add file to workspace: {file} ({e.Message})");
                    }
                }
            }

            workspace.TryApplyChanges(solution);
            Logger.Info("Fallback workspace created with " + workspace.CurrentSolution.Projects.Count() + " projects.");
            return workspace;
        }

        public static (Project, Document) GetProjectAndDocumentByName(Solution solution, string projectName, string documentName)
        {
            Project project = GetProjectByName(solution, projectName);
            Document document = GetDocumentByName(project, documentName);
            return (project, document);
        }

        public static Project GetProjectByName(Solution solution, string projectName)
        {
            return solution.Projects.FirstOrDefault(s => s.Name == projectName);
        }

        public static Document GetDocumentByName(Project project, string documentName)
        {
            if (project == null) return null;
            return project.Documents.FirstOrDefault(s => s.Name == documentName);
        }

        public static Document GetDocumentByName(Solution solution, string projectName, string documentName)
        {
            Project project = GetProjectByName(solution, projectName);
            return GetDocumentByName(project, documentName);
        }

        protected static void SetConfiguration(Solution solution, Dictionary<string, string> properties, bool isGlobal)
        {
            Logger.Debug($"VisualStudio SetConfiguration - Global is {isGlobal}");
            Logger.Debug(JsonConvert.SerializeObject(properties));
            foreach (Project project in solution.Projects)
            {
                BuildEvaluation.Project buildProject = GetBuildProject(project);
                if (buildProject != null)
                {
                    SetConfiguration(buildProject, properties, isGlobal);
                }
            }
        }

        protected static void SetConfiguration(BuildEvaluation.Project buildProject, Dictionary<string, string> properties, bool isGlobal)
        {
            foreach (KeyValuePair<string, string> property in properties)
            {
                if (isGlobal)
                {
                    buildProject.SetGlobalProperty(property.Key, property.Value);
                }
                else
                {
                    buildProject.SetProperty(property.Key, property.Value);
                }
            }
            buildProject.Save();
        }

        public static void SetProjectConfiguration(Solution solution, Dictionary<string, string> properties)
        {
            SetConfiguration(solution, properties, false);
        }

        public static BuildEvaluation.Project GetBuildProject(Project project)
        {
            try
            {
                if (String.IsNullOrEmpty(project?.FilePath))
                {
                    Logger.Warning($"GetBuildProject: project.FilePath is null or empty for project '{project?.Name}'");
                    return null;
                }

                BuildEvaluation.ProjectCollection projectCollection = new BuildEvaluation.ProjectCollection();
                return projectCollection.LoadProject(project.FilePath);
            }
            catch (Exception e)
            {
                Logger.Warning($"GetBuildProject failed for project '{project?.Name}': {e.Message}");
                return null;
            }
        }

        public static bool Build(Solution solution)
        {
            return Build(solution, new Dictionary<string, string>());
        }

        public static bool Build(Solution solution, Dictionary<string, string> properties)
        {
            Logger.Debug("");
            Logger.Debug("VisualStudio Build");
            Logger.Debug(JsonConvert.SerializeObject(properties));
            try
            {
                bool result = true;
                foreach (Project project in solution.Projects)
                {
                    BuildEvaluation.Project buildProject = GetBuildProject(project);
                    if (buildProject == null)
                    {
                        Logger.Error($"Could not obtain BuildEvaluation project for '{project.Name}' - skipping build for this project.");
                        result = false;
                        continue;
                    }
                    SetConfiguration(buildProject, properties, true);
                    if (CommandLineData.Global.Project.Debug)
                    {
                        result = result && buildProject.Build(new ConsoleLogger());
                    }
                    else
                    {
                        result = result && buildProject.Build();
                    }
                }

                return result;
            }
            catch (Exception e)
            {
                Logger.Error("ERROR", true, false);
                Logger.Error("Could not compile solution: " + e.Message);
            }
            return false;
        }
    }
}
