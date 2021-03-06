﻿using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LINQPad.Extensibility.DataContext;
using Microsoft.CSharp;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xamasoft.JsonClassGenerator;

namespace JsonDataContextDriver
{
    public class JsonDynamicDataContextDriver : DynamicDataContextDriver
    {
        public override string Name
        {
            get { return "JSON DataContext Provider"; }
        }

        public override string Author
        {
            get { return "Ryan Davis"; }
        }

        public override string GetConnectionDescription(IConnectionInfo cxInfo)
        {
            return String.IsNullOrWhiteSpace(cxInfo.DisplayName) ? "Unnamed JSON Data Context" : cxInfo.DisplayName;
        }

        public override bool ShowConnectionDialog(IConnectionInfo cxInfo, bool isNewConnection)
        {
            var dialog = new ConnectionDialog();
            dialog.SetContext(cxInfo, isNewConnection);

            var result = dialog.ShowDialog();
            return result == true;
        }

        public override List<ExplorerItem> GetSchemaAndBuildAssembly(IConnectionInfo cxInfo,
            AssemblyName assemblyToBuild, ref string nameSpace,
            ref string typeName)
        {
            var xInputs = cxInfo.DriverData.Element("inputDefs");
            if (xInputs == null)
                return new List<ExplorerItem>();

            var jss = new JsonSerializerSettings {TypeNameHandling = TypeNameHandling.All};
            var inputDefs = JsonConvert.DeserializeObject<List<JsonInput>>(xInputs.Value, jss);

            var ns = nameSpace;

            // generate class definitions
            var classDefinitions =
                inputDefs
                    .SelectMany(i => GetClassesForInput(i, ns))
                    .ToList();

            // remove the error'd inputs
            var errors = classDefinitions.Where(c => !c.Success).ToList();
            classDefinitions =
                classDefinitions
                    .Where(c => c.Success)
                    .ToList();

            // resolve duplicates
            classDefinitions
                .GroupBy(c => c.ClassName)
                .Where(c => c.Count() > 1)
                .SelectMany(cs => cs.Select((c, i) => new {Class = c, Index = i + 1}).Skip(1))
                .ToList()
                .ForEach(c => c.Class.ClassName += "_" + c.Index);

            // create code to compile
            var usings = "using System;\r\n" +
                         "using System.Collections.Generic;\r\n" +
                         "using System.IO;\r\n" +
                         "using Newtonsoft.Json;\r\n";

            var contextProperties =
                classDefinitions.Select(
                    c =>
                        String.Format(
                            "public List<{0}> {1}s {{ get {{ return JsonConvert.DeserializeObject<List<{0}>>(File.ReadAllText(@\"{2}\")); }} }}",
                            c.ClassName, c.ClassName, c.DataFilePath));

            var context =
                String.Format("namespace {1} {{\r\n\r\n public class {2} {{\r\n\r\n\t\t{0}\r\n\r\n}}\r\n\r\n}}",
                    String.Join("\r\n\r\n\t\t", contextProperties), nameSpace, typeName);
            var code = String.Join("\r\n", classDefinitions.Select(c => c.ClassDefinition));

            var contextWithCode = String.Join("\r\n\r\n", usings, context, code);

            var provider = new CSharpCodeProvider();
            var parameters = new CompilerParameters
            {
                IncludeDebugInformation = true,
                OutputAssembly = assemblyToBuild.CodeBase,
                ReferencedAssemblies =
                {
                    typeof (JsonConvert).Assembly.Location
                }
            };

            var result = provider.CompileAssemblyFromSource(parameters, contextWithCode);

            // Pray to the gods of UX for redemption..
            // We Can Do Better
            if (errors.Any())
                MessageBox.Show(String.Format("Couldn't process {0} files:\r\n{1}", errors.Count,
                    String.Join(Environment.NewLine,
                        errors.Select(e => String.Format("{0} - {1}", e.DataFilePath, e.Error.Message)))));

            return
                LinqPadSampleCode.GetSchema(
                    result.CompiledAssembly.GetType(String.Format("{0}.{1}", nameSpace, typeName)));
        }

        public List<GeneratedClass> GetClassesForInput(JsonInput input, string nameSpace)
        {
            var numSamples = input.NumRowsToSample;

            return
                GetFilesForInput(input)
                    .Select(f =>
                    {
                        // TODO: Be a better error handler
                        try
                        {
                            var fs = new FileStream(f, FileMode.Open);
                            var sr = new StreamReader(fs);
                            var jtr = new JsonTextReader(sr);

                            var examples =
                                Enumerable
                                    .Range(0, numSamples)
                                    .Select(_ =>
                                    {
                                        while (jtr.Read())
                                            if (jtr.TokenType == JsonToken.StartObject)
                                                return JObject.Load(jtr).ToString();
                                        return null;
                                    })
                                    .Where(json => json != null);

                            var examplesJson = String.Format("[{0}]", String.Join(",\r\n", examples));

                            jtr.Close();
                            sr.Close();
                            fs.Close();

                            var sanitise = new Func<Func<string, string>>(() =>
                            {
                                var replacers = new[]
                                {
                                    "\n", "'", " ", "*", "/", "-", "(", ")", ".", "!", "?", "#", ":", "+", "{", "}", "&",
                                    ","
                                };
                                var tuples = replacers.Select(r => Tuple.Create(r, "_")).ToList();

                                return originalName =>
                                {
                                    var newName = originalName.ReplaceAll(tuples);
                                    if (char.IsNumber(newName[0]))
                                        newName = "_" + newName;

                                    return newName;
                                };
                            })();

                            var className = sanitise(Path.GetFileNameWithoutExtension(f));

                            var outputStream = new MemoryStream();
                            var outputWriter = new StreamWriter(outputStream);

                            var jsg = new JsonClassGenerator
                            {
                                Example = examplesJson,
                                Namespace = nameSpace,
                                MainClass = className,
                                OutputStream = outputWriter,
                                NoHelperClass = true
                            };

                            jsg.GenerateClasses();

                            outputWriter.Flush();
                            outputStream.Seek(0, SeekOrigin.Begin);

                            var classDef = new StreamReader(outputStream)
                                .ReadToEnd()
                                .Replace("IList<", "List<")
                                .Replace(";\r\n", " { get; set; }\r\n");

                            classDef =
                                classDef.Substring(classDef.IndexOf(String.Format("namespace {0}", nameSpace),
                                    StringComparison.Ordinal));

                            return new GeneratedClass
                            {
                                ClassName = className,
                                DataFilePath = f,
                                ClassDefinition = classDef,
                                Success = true
                            };
                        }
                        catch (Exception e)
                        {
                            return new GeneratedClass
                            {
                                DataFilePath = f,
                                Success = false,
                                Error = e
                            };
                        }
                    })
                    .ToList();
        }

        public List<string> GetFilesForInput(JsonInput input)
        {
            switch (input.InputType)
            {
                case JsonInputType.File:
                    return new List<string> {input.InputPath};
                case JsonInputType.Directory:
                    return
                        Directory.GetFiles(input.InputPath, input.Mask,
                            input.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly).ToList();
                default:
                    return new List<string>();
            }
        }
    }
}