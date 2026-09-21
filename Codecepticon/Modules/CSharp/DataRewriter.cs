using Microsoft.CodeAnalysis;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Codecepticon.CommandLine;
using Codecepticon.Modules.CSharp.Rewriters;
using Codecepticon.Utils;
using Microsoft.CodeAnalysis.Text;
using System.Xml.Linq;

namespace Codecepticon.Modules.CSharp
{
    class DataRewriter
    {
        protected SyntaxTreeHelper Helper = new SyntaxTreeHelper();

        public async Task<Solution> RemoveComments(Solution solution, string projectName, string documentName)
        {
            Document document = VisualStudioManager.GetDocumentByName(solution, projectName, documentName);

            SyntaxNode syntaxRoot = await document.GetSyntaxRootAsync();
            RemoveComments rewriter = new RemoveComments();
            SyntaxNode newSyntaxRoot = rewriter.Visit(syntaxRoot);

            return solution.WithDocumentSyntaxRoot(document.Id, newSyntaxRoot);
        }

        public async Task<Solution> RewriteSwitchStatements(Solution solution, string projectName, string documentName)
        {
            Document document = VisualStudioManager.GetDocumentByName(solution, projectName, documentName);

            SyntaxNode syntaxRoot = await document.GetSyntaxRootAsync();
            SwitchStatements switchRewriter = new SwitchStatements();
            SyntaxNode newSyntaxRoot = switchRewriter.Visit(syntaxRoot);

            return solution.WithDocumentSyntaxRoot(document.Id, newSyntaxRoot);
        }

        public async Task<Solution> RewriteStrings(Solution solution, string projectName, string documentName)
        {
            Document document = VisualStudioManager.GetDocumentByName(solution, projectName, documentName);

            SyntaxNode syntaxRoot = await document.GetSyntaxRootAsync();
            Strings rewriter = new Strings();
            SyntaxNode newSyntaxRoot = rewriter.Visit(syntaxRoot);

            // Add required using statements.
            newSyntaxRoot = Helper.AddUsingStatement(newSyntaxRoot, "System");
            newSyntaxRoot = Helper.AddUsingStatement(newSyntaxRoot, "System.Linq");

            return solution.WithDocumentSyntaxRoot(document.Id, newSyntaxRoot);
        }

        public async Task<Solution> RewriteAssemblies(Solution solution, string projectName, string documentName)
        {
            Document document = VisualStudioManager.GetDocumentByName(solution, projectName, documentName);

            SyntaxNode syntaxRoot = await document.GetSyntaxRootAsync();
            Assemblies rewriter = new Assemblies();
            SyntaxNode newSyntaxRoot = rewriter.Visit(syntaxRoot);

            return solution.WithDocumentSyntaxRoot(document.Id, newSyntaxRoot);
        }

        public async Task<Solution> AddStringHelperClass(Solution solution, Project project)
        {
            string code = File.ReadAllText(CommandLineData.Global.Rewrite.Template.File);
            string mapping;

            // Find all template placeholders in format %_NAME_%
            Regex regex = new Regex(@"(%_[A-Za-z0-9_]+_%)");
            var matches = regex.Matches(code).Cast<Match>().Select(m => m.Value).ToArray().Distinct();

            // Replace each placeholder with a unique generated identifier
            foreach (var match in matches)
            {
                string name = CommandLineData.Global.NameGenerator.Generate();
                switch (match.ToLower())
                {
                    case "%_namespace_%":
                        Logger.Debug($"StringHelperClass - Replace %_namespace_% with {name}");
                        CommandLineData.Global.Rewrite.Template.Namespace = name;
                        break;
                    case "%_class_%":
                        Logger.Debug($"StringHelperClass - Replace %_class_% with {name}");
                        CommandLineData.Global.Rewrite.Template.Class = name;
                        break;
                    case "%_function_%":
                        Logger.Debug($"StringHelperClass - Replace %_function_% with {name}");
                        CommandLineData.Global.Rewrite.Template.Function = name;
                        break;
                }

                code = code.Replace(match, name);
            }

            // Inject mapping content if required by the encoding method
            switch (CommandLineData.Global.Rewrite.EncodingMethod)
            {
                case StringEncoding.StringEncodingMethods.SingleCharacterSubstitution:
                    mapping = StringEncoding.ExportSingleCharacterMap(CommandLineData.Global.Rewrite.SingleMapping, ModuleTypes.CodecepticonModules.CSharp);
                    code = code.Replace("%MAPPING%", mapping);
                    break;
                case StringEncoding.StringEncodingMethods.GroupCharacterSubstitution:
                    mapping = StringEncoding.ExportGroupCharacterMap(CommandLineData.Global.Rewrite.GroupMapping, ModuleTypes.CodecepticonModules.CSharp);
                    code = code.Replace("%MAPPING%", mapping);
                    break;
                case StringEncoding.StringEncodingMethods.ExternalFile:
                    code = code.Replace("%MAPPING%", Path.GetFileName(CommandLineData.Global.Rewrite.ExternalFile));
                    break;
            }

            SourceText source = SourceText.From(code);
            CommandLineData.Global.Rewrite.Template.AddedFile = GenerateStringFileName(project);
            string addedFileName = CommandLineData.Global.Rewrite.Template.AddedFile;

            // Compute file path relative to project directory to ensure .csproj Include attribute is correct
            string addedFilePath;
            string relativePathForCsproj = addedFileName;
            if (!string.IsNullOrEmpty(project?.FilePath))
            {
                var projectDir = Path.GetDirectoryName(project.FilePath);
                addedFilePath = Path.Combine(projectDir, addedFileName);
                // relative path from project dir for csproj Include
                relativePathForCsproj = addedFileName;
            }
            else
            {
                addedFilePath = Path.GetFullPath(addedFileName);
                relativePathForCsproj = Path.GetFileName(addedFilePath);
            }

            Logger.Debug($"File added into project for strings: {addedFileName} (path: {addedFilePath})");

            // Write the file to disk early so it can be referenced by .csproj (AdhocWorkspace may not sync automatically)
            try
            {
                var dir = Path.GetDirectoryName(addedFilePath);
                if (!String.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                File.WriteAllText(addedFilePath, code, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to write helper file to disk: {ex.Message}");
            }

            // Update .csproj to include the new helper file so MSBuild recognizes it for compilation
            if (!String.IsNullOrEmpty(project?.FilePath) && File.Exists(project.FilePath))
            {
                try
                {
                    var csprojPath = project.FilePath;
                    var xdoc = System.Xml.Linq.XDocument.Load(csprojPath);
                    XNamespace ns = xdoc.Root.GetDefaultNamespace();

                    bool alreadyIncluded = xdoc.Descendants(ns + "Compile")
                                               .Attributes("Include")
                                               .Any(a => String.Equals(a.Value.Replace('\\','/'), relativePathForCsproj.Replace('\\','/'), StringComparison.OrdinalIgnoreCase));

                    if (!alreadyIncluded)
                    {
                        var itemGroup = xdoc.Descendants(ns + "ItemGroup").FirstOrDefault();
                        if (itemGroup == null)
                        {
                            itemGroup = new System.Xml.Linq.XElement(ns + "ItemGroup");
                            xdoc.Root.Add(itemGroup);
                        }

                        var compileElem = new System.Xml.Linq.XElement(ns + "Compile");
                        compileElem.SetAttributeValue("Include", relativePathForCsproj);
                        itemGroup.Add(compileElem);

                        xdoc.Save(csprojPath);
                        Logger.Debug($"Added <Compile Include=\"{relativePathForCsproj}\" /> to {csprojPath}");
                    }
                    else
                    {
                        Logger.Debug($"Helper file already included in csproj: {relativePathForCsproj}");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warning($"Failed to update csproj to include helper file: {ex.Message}");
                }
            }
            else
            {
                Logger.Debug("Project file path not available - skipping .csproj update.");
            }

            // Add document to Roslyn solution with absolute path to enable proper document tracking
            var did = DocumentId.CreateNewId(project.Id);
            solution = solution.AddDocument(did, addedFileName, source, folders: null, filePath: addedFilePath);

            return solution;
        }

        protected string GenerateStringFileName(Project project)
        {
            // Get all the filenames.
            List<string> allFilenames = new List<string>();
            foreach (Document document in project.Documents)
            {
                allFilenames.Add(document.Name.Replace(".cs", ""));
            }

            // Randomly combine 2 names at a time until we get a unique name.
            Random rnd = new Random();
            string fileName;
            do
            {
                fileName = allFilenames[rnd.Next(allFilenames.Count)] + allFilenames[rnd.Next(allFilenames.Count)];
                if (allFilenames.Contains(fileName))
                {
                    fileName = "";
                }
            } while (fileName == "");

            return fileName + ".cs";
        }
    }
}
