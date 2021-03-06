﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml;
using EnvDTE;
using EnvDTE80;
using MLabs.ConvertToPcl.DataContracts;
using MLabs.ConvertToPcl.Extension;
using MLabs.ConvertToPcl.ViewModel;

namespace MLabs.ConvertToPcl
{
    public class PclConverter
    {
        private readonly DTE applicationObject;
        private ProjectsUpdateList projectsUpdateList;
        private List<NetFramework> frameworkModels;
        private List<PortableFramework> portableFrameworkModels;

        private object syncRoot = new object();

        public PclConverter(DTE applicationObject)
        {
            this.applicationObject = applicationObject;

            frameworkModels = new List<NetFramework>();
            portableFrameworkModels = new List<PortableFramework>();

            var folderPath = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

            var frameworks = new XmlDocument();
            frameworks.Load(Path.Combine(folderPath, "Frameworks.xml"));
            foreach (XmlNode node in frameworks.DocumentElement.ChildNodes)
                frameworkModels.Add(new NetFramework
                {
                    Id = uint.Parse(node.Attributes["Id"].Value),
                    Name = node.Attributes["Name"].Value
                });

            frameworks = new XmlDocument();
            frameworks.Load(Path.Combine(folderPath, "FrameworksPortable.xml"));
            foreach (XmlNode node in frameworks.DocumentElement.ChildNodes)
                portableFrameworkModels.Add(new PortableFramework
                {
                    Name = node.Attributes["Name"].Value,
                    Description = node.Attributes["Description"].Value
                });

        }

        private bool isSolutionLoaded = true;
        private SynchronizationContext synchronizationContext;

        public void Show()
        {
            lock (syncRoot)
            {
                synchronizationContext = SynchronizationContext.Current;

                projectsUpdateList = new ProjectsUpdateList();

                projectsUpdateList.UpdateFired += Update;
                projectsUpdateList.ReloadFired += ReloadProjects;

                projectsUpdateList.FrameworksInUse = frameworkModels;
                projectsUpdateList.PossiblePortableFrameworks = portableFrameworkModels;

                projectsUpdateList.State = "Waiting all projects are loaded...";

                if (applicationObject.Solution == null)
                {
                    projectsUpdateList.State = "No solution";
                }
                else
                {
                    if (isSolutionLoaded)
                        ReloadProjects();
                }

                projectsUpdateList.StartPosition = FormStartPosition.CenterScreen;
                projectsUpdateList.TopMost = true;
                projectsUpdateList.ShowDialog();

            }
        }

        public void OnBeforeSolutionLoaded()
        {
            lock (syncRoot)
            {
                if (projectsUpdateList != null)
                    projectsUpdateList.State = "Waiting all projects are loaded...";

                isSolutionLoaded = false;

            }
        }

        public void OnAfterSolutionLoaded()
        {
            lock (syncRoot)
            {
                isSolutionLoaded = true;

                if (projectsUpdateList != null && projectsUpdateList.Visible)
                    ReloadProjects();
            }
        }

        private void ReloadProjects()
        {
            var projectModels = LoadProjects();

            projectsUpdateList.State = projectModels.Count == 0 ? "No .Net projects" : String.Empty;

            projectsUpdateList.Projects = projectModels;
        }

        private List<ProjectModel> LoadProjects()
        {
            Projects projects = applicationObject.Solution.Projects;

            if (projects.Count == 0)
            {
                return new List<ProjectModel>();
            }

            var projectModels = MapProjects(projects.OfType<Project>());

            projectModels = projectModels
                .Where(pm => pm.HasFramework)
                .ToList();
            return projectModels;
        }

        private List<ProjectModel> MapProjects(IEnumerable<Project> projects)
        {
            var projectModels = new List<ProjectModel>();
            foreach (var p in projects)
            {
                if (p == null)
                    continue;

                if (p.Kind == ProjectKinds.vsProjectKindSolutionFolder)
                {
                    var projectItems = p.ProjectItems.OfType<ProjectItem>();
                    var subProjects = projectItems.Select(pi => pi.SubProject);
                    projectModels.AddRange(MapProjects(subProjects));
                }
                else
                {
                    var projectModel = MapProject(p);
                    projectModels.Add(projectModel);
                }
            }
            return projectModels;
        }

        private static ProjectModel MapProject(Project p)
        {
            var projectModel = new ProjectModel
            {
                Name = p.Name,
                DteProject = p,
            };
            if (p.Properties != null)
            {
                try
                {
                    var frameworkModel = new NetFramework
                    {
                        Id = (uint) p.Properties.Item("TargetFramework").Value,
                        Name = (string) p.Properties.Item("TargetFrameworkMoniker").Value
                    };
                    projectModel.NetFrameworkInUse = frameworkModel;
                }
                catch (ArgumentException e) //possible when project still loading
                {
                    Debug.WriteLine("ArgumentException on " + projectModel + e);
                }
                catch (InvalidCastException e) //for some projects with wrong types
                {
                    Debug.WriteLine("InvalidCastException on " + projectModel + e);
                }
            }
            return projectModel;
        }

        private async void Update()
        {
            var portableFramework = projectsUpdateList.SelectedPortableFramework;

            projectsUpdateList.State = "Updating...";

            await UpdateFrameworks(portableFramework);
            projectsUpdateList.Projects = LoadProjects();

            projectsUpdateList.State = "Done. Please close Dialog and reload Projects.";
        }

        private Task UpdateFrameworks(PortableFramework portFramework)
        {
            return Task.Run(() =>
            {
                var selectedProjects = projectsUpdateList.Projects.Where(p => p.IsSelected && p.NetFrameworkInUse.Name.Contains("4.5"));

                foreach (var projectModel in selectedProjects)
                {
                    try
                    {
                        projectModel.DteProject.Save(projectModel.DteProject.FullName);
                        if (!projectModel.NetFrameworkInUse.Name.ToLower().Contains("port"))
                        {
                            ChangeAssemblyFile(projectModel.DteProject);
                            RemoveFrameworkReference(projectModel.DteProject);
                        }
                        ChangeProjectFile(projectModel.DteProject, portFramework.Name);

                        synchronizationContext.Post(o =>
                        {
                            var pm = (ProjectModel) o;
                            projectsUpdateList.State = string.Format("Updating... {0} done", pm.Name);
                        }, projectModel);
                    }
                    catch (COMException e) //possible "project unavailable" for unknown reasons
                    {
                        Debug.WriteLine("COMException on " + projectModel.Name + e);
                    }
                }
            });
        }

        private void ChangeAssemblyFile(Project project)
        {
            var items = GetProjectItemsRecursively(project.ProjectItems);

            foreach (var element in items)
            {
                if (element != null && element.FileCount > 0)
                {
                    var assemblycs = element.FileNames[1];

                    if (assemblycs.Contains("AssemblyInfo.cs"))
                    {
                        bool isChanged;
                        string fileContent;
                        fileContent = ChangeAssemblyContent(assemblycs, out isChanged);
                        if(isChanged)
                        {
                            element.Open();
                            element.Save();
                            var editDoc = (TextDocument) element.Document.Object("TextDocument");
                            var editPoint = (EditPoint) editDoc.StartPoint.CreateEditPoint();
                            var endPoint = (EditPoint) editDoc.EndPoint.CreateEditPoint();
                            editPoint.Delete(endPoint);
                            endPoint.Insert(fileContent);
                            element.Save();
                        }
                    }
                }
            }
        }

        private static string ChangeAssemblyContent(string assemblycs, out bool isChanged)
        {
            string fileContent;
            using (var sr = new StreamReader(assemblycs))
            {
                fileContent = sr.ReadToEnd();
                var fileContentOrg = fileContent;
                fileContent = fileContent.Replace("[assembly: ComVisible(false)]", string.Empty);
                var pos1 = fileContent.IndexOf("[assembly: Guid(");
                if (pos1 > 0)
                {
                    var pos2 = fileContent.IndexOf(")]", pos1);
                    fileContent = fileContent.Remove(pos1, pos2 - pos1 + 2);
                }
                isChanged = fileContent != fileContentOrg;
            }
            return fileContent;
        }

        private List<ProjectItem> GetProjectItemsRecursively(ProjectItems items)
        {
            var result = new List<ProjectItem>();
            if (items == null) return result;

            foreach (ProjectItem item in items)
            {
                result.Add(item);
                result.AddRange(GetProjectItemsRecursively(item.ProjectItems));
            }
            return result;
        }


        private void RemoveFrameworkReference(Project project)
        {
            var vsproject = project.Object as VSLangProj.VSProject;

            if (vsproject == null) return;
            foreach (VSLangProj.Reference reference in vsproject.References)
            {
                if (reference.SourceProject == null)
                {
                    var fullName = GetFullName(reference);
                    var assemblyName = new AssemblyName(fullName);
                    var isFrameworkAssembly = IsFrameworkAssembly(assemblyName);
                    if (isFrameworkAssembly && !assemblyName.Name.Contains("mscorlib"))
                    {
                        try
                        {
                            reference.Remove();
                        }
                        catch
                        {
                        }
                    }
                }
            }
            project.Save();
        }

        private static bool IsFrameworkAssembly(AssemblyName assemblyName)
        {
            try
            {
                var assembly = Assembly.Load(assemblyName);
                var attribute =
                    assembly.GetCustomAttributes(typeof (AssemblyProductAttribute), false)[0] as
                        AssemblyProductAttribute;
                var isFrameworkAssembly = attribute != null && (attribute.Product == "Microsoft® .NET Framework");
                return isFrameworkAssembly;
            }
            catch (FileNotFoundException)
            {
                // unavailable third party tools
                return false;

            }
            catch (Exception ex)
            {
                Debug.WriteLine("IsFrameworkAssembly exception" + ex);
                return false;
            }
        }

        private static string GetFullName(VSLangProj.Reference reference)
        {
            return string.Format("{0}, Version={1}.{2}.{3}.{4}, Culture={5}, PublicKeyToken={6}",
                reference.Name,
                reference.MajorVersion, reference.MinorVersion, reference.BuildNumber, reference.RevisionNumber,
                reference.Culture.Or("neutral"),
                reference.PublicKeyToken.Or("null"));
        }
        
        /// <summary>
        /// Change entries in csproj file. Refactor this. How can CsProj Entries direct changed?
        /// </summary>
        /// <param name="project"></param>
        /// <param name="frameWorkVersion"></param>
        private void ChangeProjectFile(Project project, string frameWorkVersion)
        {
            string path = project.FullName;
            var fileLines = new List<string>();
            using (var sr = new StreamReader(path))
            {
                while (!sr.EndOfStream)
                {
                    fileLines.Add(sr.ReadLine());
                }
            }

            var fileContent = ReplaceSettings(frameWorkVersion, fileLines);

            // Checkout file
            project.Save();
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
            {
                attributes = RemoveAttribute(attributes, FileAttributes.ReadOnly);
                File.SetAttributes(path, attributes);
            }

            // work with new file to force a reload  
            var backupFile = path + "bak";
            if (File.Exists(backupFile))
            {
                File.Delete(backupFile);
            }
            File.Move(path, backupFile);
            using (var sw = new StreamWriter(path))
            {
                sw.Write(fileContent);
            }
        }

        private static string ReplaceSettings(string frameWorkVersion, List<string> fileLines)
        {
            var exist = fileLines.Any(t => t.Contains(@"<TargetFrameworkVersion>v4.5</TargetFrameworkVersion>"));
            if (!exist) return ConvertToString(fileLines);
            
            const string startImportProject = @"<Import Project=""$(MSBuildToolsPath)";
            const string startImportProjectOld = @"<Import Project=""$(MSBuildBinPath)";
                                                 
            const string tab = "    ";
            const string newStartProject =
                @"<Import Project=""$(MSBuildExtensionsPath32)\Microsoft\Portable\$(TargetFrameworkVersion)\Microsoft.Portable.CSharp.targets"" />";
            const string projectTypePCL =
    @"<ProjectTypeGuids>{786C830F-07A1-408B-BD7F-6EE04809D6DB};{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}</ProjectTypeGuids>";

            var isPclAlready = fileLines.Any(t => t.Contains(newStartProject)) || fileLines.Any(t => t.Contains(projectTypePCL));

            if (!isPclAlready)
            {
                ReplaceLineIfExist(fileLines, startImportProject, tab + newStartProject);
                ReplaceLineIfExist(fileLines, startImportProjectOld, tab + newStartProject);

                const string EmptyTarget = "<TargetFrameworkProfile />";
                int pos = fileLines.FindIndex(t => t.Contains((EmptyTarget)));
                if (pos > 0)
                {
                    fileLines.RemoveAt(pos);
                }
                else
                {
                    pos = fileLines.FindIndex(t => t.Contains(@"<TargetFrameworkVersion>v4.5</TargetFrameworkVersion>"));
                }

                var projectType = string.Format("{0}{1}", tab, projectTypePCL);
                fileLines.Insert(pos, projectType);

                var frameWork = string.Format("{0}<TargetFrameworkProfile>{1}</TargetFrameworkProfile>",tab, frameWorkVersion);
                fileLines.Insert(pos, frameWork);
            }

            return ConvertToString(fileLines);
        }

        private static void ReplaceLineIfExist(List<string> fileLines, string startImportProject, string replaceWith)
        {
            var pos = fileLines.FindIndex(t => t.Contains(startImportProject));
            if (pos > 0)
            {
                fileLines.RemoveAt(pos);
                fileLines.Insert(pos, replaceWith);
            }
        }

        private static string ConvertToString(IEnumerable<string> lines)
        {
            string result = string.Empty;
            foreach (var line in lines)
            {
                result += line + Environment.NewLine;
            }

            return result;
        }

        private static FileAttributes RemoveAttribute(FileAttributes attributes, FileAttributes attributesToRemove)
        {
            return attributes & ~attributesToRemove;
        }
    }

}