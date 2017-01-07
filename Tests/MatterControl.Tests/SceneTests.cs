﻿/*
Copyright (c) 2016, John Lewin
All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice, this
   list of conditions and the following disclaimer.
2. Redistributions in binary form must reproduce the above copyright notice,
   this list of conditions and the following disclaimer in the documentation
   and/or other materials provided with the distribution.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND
ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR
ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
(INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

The views and conclusions contained in the software and documentation are those
of the authors and should not be interpreted as representing official policies,
either expressed or implied, of the FreeBSD Project.
*/

using MatterHackers.Agg.PlatformAbstract;
using MatterHackers.DataConverters3D;
using MatterHackers.MatterControl.PartPreviewWindow;
using MatterHackers.MeshVisualizer;
using MatterHackers.PolygonMesh.Csg;
using MatterHackers.VectorMath;
using Newtonsoft.Json;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MatterHackers.MatterControl.Tests.Automation;
using MatterHackers.Agg;
using MatterHackers.MatterControl.SlicerConfiguration;

namespace MatterHackers.PolygonMesh.UnitTests
{
	[TestFixture, Category("Agg.PolygonMesh")]
	public class SceneTests
	{
		static string matterControlPath = Path.GetFullPath(Path.Combine("..", "..", "..", "..", "..", "MatterControl"));

		[Test]
		public void SaveSimpleScene()
		{
			var scene = new InteractiveScene();
			scene.Children.Add(new Object3D
			{
				ItemType = Object3DTypes.Model
			});

			string tempPath = GetSceneTempPath();
			string filePath = Path.Combine(tempPath, "some.mcx");

			scene.Save(filePath, tempPath);

			Assert.IsTrue(File.Exists(filePath));

			IObject3D loadedItem = Object3D.Load(filePath);
			Assert.IsTrue(loadedItem.Children.Count == 1);
		}

		[Test]
		public void CreatesAndLinksAmfsForUnsavedMeshes()
		{
			var scene = new InteractiveScene();
			scene.Children.Add(new Object3D
			{
				ItemType = Object3DTypes.Model,
				Mesh = PlatonicSolids.CreateCube(20, 20, 20)
			});

			string tempPath = GetSceneTempPath();
			string filePath = Path.Combine(tempPath, "some.mcx");

			scene.Save(filePath, tempPath);

			Assert.IsTrue(File.Exists(filePath));

			IObject3D loadedItem = Object3D.Load(filePath);
			Assert.IsTrue(loadedItem.Children.Count == 1);

			IObject3D meshItem = loadedItem.Children.First();

			Assert.IsTrue(!string.IsNullOrEmpty(meshItem.MeshPath));
			Assert.IsTrue(File.Exists(meshItem.MeshPath));
			Assert.IsNotNull(meshItem.Mesh);
			Assert.IsTrue(meshItem.Mesh.Faces.Count > 0);
		}

		[Test]
		public async Task ResavedSceneRemainsConsistent()
		{
#if !__ANDROID__
			string staticDataPathOverride = Path.Combine(matterControlPath, "StaticData");

			// Set the static data to point to the directory of MatterControl
			StaticData.Instance = new FileSystemStaticData(TestContext.CurrentContext.ResolveProjectPath(4, "StaticData"));
			MatterControlUtilities.OverrideAppDataLocation(TestContext.CurrentContext.ResolveProjectPath(4));

			ActiveSliceSettings.Instance?.SetValue(SettingsKey.center_part_on_bed, "0");
#endif
			var view3DWidget = new View3DWidget(
				null,
				new Vector3(200, 200, 100),
				new Vector2(100, 100),
				MeshVisualizer.BedShape.Rectangular,
				View3DWidget.WindowMode.Embeded,
				 View3DWidget.AutoRotate.Disabled,
				  View3DWidget.OpenMode.Editing);

			var scene = view3DWidget.Scene;
			scene.Children.Add(new Object3D
			{
				ItemType = Object3DTypes.Model,
				Mesh = PlatonicSolids.CreateCube(20, 20, 20)
			});

			string tempPath = GetSceneTempPath();
			string filePath = Path.Combine(tempPath, "some.mcx");

			// Empty temp folder
			foreach(string tempFile in Directory.GetFiles(tempPath).ToList())
			{
				File.Delete(tempFile);
			}

			scene.Save(filePath, tempPath);
			Assert.AreEqual(1, Directory.GetFiles(tempPath).Length, "Only .mcx file should exists");
			Assert.AreEqual(1, Directory.GetFiles(Path.Combine(tempPath, "Assets")).Length, "Only 1 asset should exist");

			var originalFiles = Directory.GetFiles(tempPath).ToArray(); ;
			
			IObject3D loadedItem = Object3D.Load(filePath);
			Assert.IsTrue(loadedItem.Children.Count == 1);

			await view3DWidget.ClearBedAndLoadPrintItemWrapper(
				new MatterControl.PrintQueue.PrintItemWrapper(
					new MatterControl.DataStorage.PrintItem("test", filePath)), true);

			string onDiskData = JsonConvert.SerializeObject(loadedItem, Formatting.Indented);
			string inMemoryData = JsonConvert.SerializeObject(view3DWidget.Scene, Formatting.Indented);

			Assert.IsTrue(inMemoryData == onDiskData);

			// Save the scene a second time, validate that things remain the same
			view3DWidget.Scene.Save(filePath, tempPath);
			onDiskData = JsonConvert.SerializeObject(loadedItem, Formatting.Indented);

			Assert.IsTrue(inMemoryData == onDiskData);

			// Verify that no additional files get created on second save
			Assert.AreEqual(1, Directory.GetFiles(tempPath).Length, "Only .mcx file should exists");
			Assert.AreEqual(1, Directory.GetFiles(Path.Combine(tempPath, "Assets")).Length, "Only 1 asset should exist");
		}

		public static string GetSceneTempPath()
		{
			string tempPath = Path.GetFullPath(Path.Combine(matterControlPath, "Tests", "temp", "scenetests"));
			Directory.CreateDirectory(tempPath);
			return tempPath;
		}
	}
}