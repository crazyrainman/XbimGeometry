﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Xbim.Common;
using Xbim.Common.Logging;
using Xbim.Ifc;
using Xbim.IO;
using Xbim.IO.Esent;
using Xbim.ModelGeometry.Scene;

namespace Xbim.Geometry.Profiler
{

    class Program
    {
        internal static readonly ILogger Logger = LoggerFactory.GetLogger();

        /// <summary>
        /// Converts an Ifc File to xBIM if it does not already exists, then converts the geoemtry to Xbim format and profiles the results
        /// </summary>
        /// <param name="args"> file[.ifc, xbim]</param>

        private static void Main(string[] args)
        {
            if (args.Length < 1)
            {
                Console.WriteLine("No Ifc or xBim file specified");
                return;
            }

            var writeXbim = args.Any(t => t.ToLowerInvariant() == "/keepxbim");
            var singleThread = args.Any(t => t.ToLowerInvariant() == "/singlethread");
            var writeInInputFolder = args.Any(t => t.ToLowerInvariant() == "/samefolder");

            foreach (var arg in args)
            {
                if (!arg.StartsWith("/"))
                    Profile(arg, writeXbim, writeInInputFolder, singleThread);
            }
            
            Console.WriteLine("Press any key to exit");
            Console.Read();
        }

        private static Xbim3DModelContext.MeshingBehaviourResult SkipRebar(int elementId, int typeId, ref double linearDeflection, ref double angularDeflection)
        {
            var shortTypeId = Convert.ToInt16(typeId);
            if (shortTypeId == 571) // skip IfcReinforcingbar
                return Xbim3DModelContext.MeshingBehaviourResult.Skip;
            return Xbim3DModelContext.MeshingBehaviourResult.Default;
        }

        private static void Profile(string fileNameInput, bool writeXbim, bool writeInInputFolder, bool singleThread)
        {
            var mainStopWatch = new Stopwatch();
            mainStopWatch.Start();

            var todo = new List<string>();

            if (Directory.Exists(fileNameInput))
            {
                todo.AddRange(Directory.EnumerateFiles(fileNameInput, "*.ifc"));
                if (!writeXbim)
                    todo.AddRange(Directory.EnumerateFiles(fileNameInput, "*.xbim"));
            }
            else if (File.Exists(fileNameInput))
            {
                todo.Add(fileNameInput);
            }   

            foreach (var fileName in todo)
            {
                Logger.InfoFormat("Opening \t{0}", fileName);
                using (var model = GetModel(fileName, writeXbim))
                {
                    Logger.InfoFormat("Parse Time \t{0:0.0} ms", mainStopWatch.ElapsedMilliseconds);
                    if (model != null)
                    {
                        int prevProgress = -1;
                        var functionStack = new ConcurrentStack<Tuple<string, double>>();
                        ReportProgressDelegate progDelegate = delegate (int percentProgress, object userState)
                        {
                            if (percentProgress == -1)
                            {
                                functionStack.Push(new Tuple<string, double>(userState.ToString(),
                                    DateTime.Now.TimeOfDay.TotalMilliseconds));
                                Logger.InfoFormat("Entering - {0}", userState.ToString());
                            }
                            else if (percentProgress == 101)
                            {
                                Tuple<string, double> func;
                                if (functionStack.TryPop(out func))
                                    Logger.InfoFormat("Complete in \t{0:0.0} ms",
                                        DateTime.Now.TimeOfDay.TotalMilliseconds - func.Item2);
                            }
                            else
                            {
                                if (prevProgress != percentProgress)
                                {
                                    Tuple<string, double> func;
                                    if (functionStack.TryPeek(out func))
                                    {
                                        Logger.InfoFormat("{1}% in \t{0:0.0} ms",
                                            DateTime.Now.TimeOfDay.TotalMilliseconds - func.Item2,
                                            percentProgress
                                            );
                                    }
                                    prevProgress = percentProgress;
                                }
                            }
                        };
                        var context = new Xbim3DModelContext(model);
                        if (singleThread)
                            context.MaxThreads = 1;
                        context.UseSimplifiedFastExtruder = true;

                        // context.CustomMeshingBehaviour += SkipRebar;
                        context.CreateContext(progDelegate: progDelegate);

                        mainStopWatch.Stop();
                        Logger.InfoFormat("Xbim total Compile Time \t{0:0.0} ms", mainStopWatch.ElapsedMilliseconds);

                        var wexBimFilename = Path.ChangeExtension(fileName, "wexBIM");
                        using (var wexBiMfile = new FileStream(wexBimFilename, FileMode.Create, FileAccess.Write))
                        {
                            using (var wexBimBinaryWriter = new BinaryWriter(wexBiMfile))
                            {
                                var stopWatch = new Stopwatch();
                                Logger.InfoFormat("Entering -  Create wexBIM");
                                stopWatch.Start();
                                model.SaveAsWexBim(wexBimBinaryWriter);

                                stopWatch.Stop();
                                Logger.InfoFormat("Complete - in \t{0:0.0} ms", stopWatch.ElapsedMilliseconds);
                                wexBimBinaryWriter.Close();
                            }
                            wexBiMfile.Close();
                        }
                        if (writeXbim)
                        {
                            string fName;
                            if (!writeInInputFolder)
                            {
                                fName = Path.GetFileNameWithoutExtension(fileName);
                                fName = Path.ChangeExtension(fName, "xbim");
                            }
                            else
                            {
                                fName = Path.ChangeExtension(fileName, "xbim");
                            }
                            model.SaveAs(fName, IfcStorageType.Xbim);
                        }
                        model.Close();
                    }
                    Debug.Assert(EsentModel.ModelOpenCount == 0);
                }
            }
        }

        private static IfcStore GetModel(string fileName, bool writeXbim)
        {
            var extension = Path.GetExtension(fileName);
            if (string.IsNullOrWhiteSpace(extension))
            {
                if (File.Exists(Path.ChangeExtension(fileName, "xbim"))) //use xBIM if exists
                    fileName = Path.ChangeExtension(fileName, "xbim");
                else if (File.Exists(Path.ChangeExtension(fileName, "ifc"))) //use ifc if exists
                    fileName = Path.ChangeExtension(fileName, "ifc");
                else if (File.Exists(Path.ChangeExtension(fileName, "ifczip"))) //use ifczip if exists
                    fileName = Path.ChangeExtension(fileName, "ifczip");
                else if (File.Exists(Path.ChangeExtension(fileName, "ifcxml"))) //use ifcxml if exists
                    fileName = Path.ChangeExtension(fileName, "ifcxml");
            }

            if (!File.Exists(fileName)) 
                return null;
                extension = Path.GetExtension(fileName);
            if (string.Compare(extension, ".xbim", StringComparison.OrdinalIgnoreCase) == 0) //just open xbim
                {
                    try
                    {
                        return IfcStore.Open(fileName);                      
                    }
                    catch (Exception e)
                    {
                    var message = string.Format("Unable to open model {0}, {1}", fileName, e.Message);
                    Logger.Error(message, e);
                    Console.WriteLine(message);
                    }
                }
                else //we need to create the store
                {
                    try
                    {
                        double? threshhold=null;
                        if(writeXbim) threshhold = 0; //otherwise let it do what it needs to based on size
                       var model = IfcStore.Open(fileName, null, threshhold); 
                      
                        return model;
                    }
                    catch (Exception e)
                    {
                    var message = string.Format("Unable to open model {0}, {1}", fileName, e.Message);
                    Logger.Error(message, e);
                    Console.WriteLine(message);
                }
            }
            return null;
        }
    }
}
