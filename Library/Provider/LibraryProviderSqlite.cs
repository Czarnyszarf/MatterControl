﻿/*
Copyright (c) 2015, Lars Brubaker
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

using MatterHackers.Agg;
using MatterHackers.Agg.Image;
using MatterHackers.Agg.PlatformAbstract;
using MatterHackers.Agg.UI;
using MatterHackers.MatterControl.DataStorage;
using MatterHackers.MatterControl.PrintQueue;
using MatterHackers.MatterControl.SettingsManagement;
using MatterHackers.PolygonMesh;
using MatterHackers.PolygonMesh.Processors;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace MatterHackers.MatterControl.PrintLibrary.Provider
{
	public class LibraryProviderSQLite : LibraryProvider
	{
		private static LibraryProviderSQLite instance = null;
		private PrintItemCollection baseLibraryCollection;

		private List<PrintItemCollection> childCollections = new List<PrintItemCollection>();
		private string keywordFilter = string.Empty;

		private List<PrintItemWrapper> printItems = new List<PrintItemWrapper>();

		public LibraryProviderSQLite(PrintItemCollection baseLibraryCollection, LibraryProvider parentLibraryProvider)
			: base(parentLibraryProvider)
		{
			if (baseLibraryCollection == null)
			{
				baseLibraryCollection = GetRootLibraryCollection2(this);
			}

			this.baseLibraryCollection = baseLibraryCollection;
			LoadLibraryItems();
		}

		public static LibraryProvider Instance
		{
			get
			{
				if (instance == null)
				{
					instance = new LibraryProviderSQLite(null, null);
				}

				return instance;
			}
		}

		public static string StaticProviderKey
		{
			get
			{
				return "LibraryProviderSqliteKey";
			}
		}

		public override int CollectionCount
		{
			get
			{
				return childCollections.Count;
			}
		}

		public override int ItemCount
		{
			get
			{
				return printItems.Count;
			}
		}

		public override string KeywordFilter
		{
			get
			{
				return keywordFilter;
			}

			set
			{
				keywordFilter = value;
			}
		}

		public override string Name
		{
			get
			{
				return "Local Library";
			}
		}

		public override string ProviderData
		{
			get 
			{
				return baseLibraryCollection.Id.ToString();
			}
		}

		public override string ProviderKey
		{
			get
			{
				return StaticProviderKey;
			}
		}

		public static IEnumerable<PrintItem> GetAllPrintItemsRecursive()
		{
			// NOTE: We are making the assumption that everything is reference if it does not have a 0 in eth PrintItemCollectionID.
			string query = "SELECT * FROM PrintItem WHERE PrintItemCollectionID != 0;";
			IEnumerable<PrintItem> result = (IEnumerable<PrintItem>)Datastore.Instance.dbSQLite.Query<PrintItem>(query);
			return result;
		}

		static PrintItemCollection GetRootLibraryCollection2(LibraryProviderSQLite rootLibrary)
		{
			// Attempt to initialize the library from the Datastore if null
			PrintItemCollection rootLibraryCollection = Datastore.Instance.dbSQLite.Table<PrintItemCollection>().Where(v => v.Name == "_library").Take(1).FirstOrDefault();

			// If the _library collection is still missing, create and populate it with default content
			if (rootLibraryCollection == null)
			{
				rootLibraryCollection = new PrintItemCollection();
				rootLibraryCollection.Name = "_library";
				rootLibraryCollection.Commit();

				// Preload library with Oem supplied list of default parts
				string[] itemsToAdd = SyncCalibrationFilesToDisk(OemSettings.Instance.PreloadedLibraryFiles);
				if (itemsToAdd.Length > 0)
				{
					// Import any files sync'd to disk into the library, then add them to the queue
					rootLibrary.AddFilesToLibrary(itemsToAdd);
				}
			}

			return rootLibraryCollection;
		}

		static public void SaveToLibraryFolder(PrintItemWrapper printItemWrapper, List<MeshGroup> meshGroups, bool AbsolutePositioned)
		{
			string[] metaData = { "Created By", "MatterControl" };
			if (AbsolutePositioned)
			{
				metaData = new string[] { "Created By", "MatterControl", "BedPosition", "Absolute" };
			}
			if (printItemWrapper.FileLocation.Contains(ApplicationDataStorage.Instance.ApplicationLibraryDataPath))
			{
				MeshOutputSettings outputInfo = new MeshOutputSettings(MeshOutputSettings.OutputType.Binary, metaData);
				MeshFileIo.Save(meshGroups, printItemWrapper.FileLocation, outputInfo);
			}
			else // save a copy to the library and update this to point at it
			{
				string fileName = Path.ChangeExtension(Path.GetRandomFileName(), ".amf");
				printItemWrapper.FileLocation = Path.Combine(ApplicationDataStorage.Instance.ApplicationLibraryDataPath, fileName);

				MeshOutputSettings outputInfo = new MeshOutputSettings(MeshOutputSettings.OutputType.Binary, metaData);
				MeshFileIo.Save(meshGroups, printItemWrapper.FileLocation, outputInfo);

				printItemWrapper.PrintItem.Commit();

				// let the queue know that the item has changed so it load the correct part
				QueueData.Instance.SaveDefaultQueue();
			}

			printItemWrapper.OnFileHasChanged();
		}

		public static string[] SyncCalibrationFilesToDisk(List<string> calibrationPrintFileNames)
		{
			// Ensure the CalibrationParts directory exists to store/import the files from disk
			string tempPath = Path.Combine(ApplicationDataStorage.Instance.ApplicationUserDataPath, "data", "temp", "calibration-parts");
			Directory.CreateDirectory(tempPath);

			// Build a list of temporary files that should be imported into the library
			return calibrationPrintFileNames.Where(fileName =>
			{
				// Filter out items that already exist in the library
				LibraryProviderSQLite rootLibrary = new LibraryProviderSQLite(null, null);
				return rootLibrary.GetLibraryItems(Path.GetFileNameWithoutExtension(fileName)).Count() <= 0;
			}).Select(fileName =>
			{
				// Copy calibration prints from StaticData to the filesystem before importing into the library
				string tempFile = Path.Combine(tempPath, Path.GetFileName(fileName));
				using (FileStream outstream = File.OpenWrite(tempFile))
				using (Stream instream = StaticData.Instance.OpenSteam(Path.Combine("OEMSettings", "SampleParts", fileName)))
				{
					instream.CopyTo(outstream);
				}

				// Project the new filename to the output
				return tempFile;
			}).ToArray();
		}

		public override void AddCollectionToLibrary(string collectionName)
		{
			PrintItemCollection newCollection = new PrintItemCollection(collectionName, "");
			newCollection.ParentCollectionID = baseLibraryCollection.Id;
			newCollection.Commit();
			LoadLibraryItems();
		}

		public override async void AddFilesToLibrary(IList<string> files, ReportProgressRatio reportProgress = null)
		{
			if (files != null && files.Count > 0)
			{
				// create enough info to show that we have items pending (maybe use names from this file list for them)
				// refresh the display to show the pending items
				//LibraryProvider.OnDataReloaded(null);

				await Task.Run(() => loadFilesIntoLibraryBackgoundWorker_DoWork(files));

				if (baseLibraryCollection != null)
				{
					LoadLibraryItems();
					LibraryProvider.OnDataReloaded(null);
				}
			}
		}

		public override void AddItem(PrintItemWrapper itemToAdd)
		{
			throw new NotImplementedException();
			LibraryProvider.OnDataReloaded(null);
		}

		void SetProviderLocator(PrintItemWrapper itemToSetProviderLocatorOn)
		{
			List<ProviderLocatorNode> providerLocator = GetProviderLocator();
			string providerLocatorJson = JsonConvert.SerializeObject(providerLocator);
			itemToSetProviderLocatorOn.PrintItem.LibraryProviderLocatorJson = providerLocatorJson;
		}

		public void AddItem(PrintItemWrapper item, int indexToInsert = -1)
		{
			if (indexToInsert == -1)
			{
				indexToInsert = printItems.Count;
			}
			SetProviderLocator(item);
			printItems.Insert(indexToInsert, item);
			// Check if the collection we are adding to is the the currently visible collection.
			List<ProviderLocatorNode> currentDisplayedCollection = GetProviderLocator();
			if (currentDisplayedCollection.Count > 0 && currentDisplayedCollection[0].Key == LibraryProviderSQLite.StaticProviderKey)
			{
				//OnItemAdded(new IndexArgs(indexToInsert));
			}
			item.PrintItem.PrintItemCollectionID = baseLibraryCollection.Id;
			item.PrintItem.Commit();
		}

		public override PrintItemCollection GetCollectionItem(int collectionIndex)
		{
			return childCollections[collectionIndex];
		}

		public override PrintItemWrapper GetPrintItemWrapper(int index)
		{
			if (index >= 0 && index < printItems.Count)
			{
				return printItems[index];
			}

			return null;
		}

		public override LibraryProvider GetProviderForItem(PrintItemCollection collection)
		{
			return new LibraryProviderSQLite(collection, this);
		}

		public void LoadLibraryItems()
		{
			printItems.Clear();
			IEnumerable<PrintItem> partFiles = GetLibraryItems(KeywordFilter);
			if (partFiles != null)
			{
				foreach (PrintItem part in partFiles)
				{
					PrintItemWrapper item = new PrintItemWrapper(part);
					SetProviderLocator(item);
					printItems.Add(item);
				}
			}

			childCollections.Clear();
			GetChildCollections();
			IEnumerable<PrintItemCollection> collections = GetChildCollections();
			if(collections != null)
			{
				childCollections.AddRange(collections);
			}

			LibraryProvider.OnDataReloaded(null);
		}

		public override void RemoveCollection(PrintItemCollection collectionToRemove)
		{
			collectionToRemove.Delete();
			LoadLibraryItems();
			LibraryProvider.OnDataReloaded(null);
		}

		public override void RemoveItem(PrintItemWrapper printItemWrapper)
		{
			int index = printItems.IndexOf(printItemWrapper);
			if (index < 0)
			{
				// It may be possible to have the same item in the remove list twice.
				// so if it is not in the PrintItems then ignore it.
				return;
			}
			printItems.RemoveAt(index);

			// and remove it from the data base
			printItemWrapper.Delete();

			LoadLibraryItems();
			LibraryProvider.OnDataReloaded(null);
		}

		public override void SaveToLibrary(PrintItemWrapper printItemWrapper, List<MeshGroup> meshGroupsToSave, List<ProviderLocatorNode> providerSavePath)
		{
			throw new NotImplementedException();
		}

		private static void AddStlOrGcode(LibraryProviderSQLite libraryToAddTo, string loadedFileName, string extension)
		{
			PrintItem printItem = new PrintItem();
			printItem.Name = Path.GetFileNameWithoutExtension(loadedFileName);
			printItem.FileLocation = Path.GetFullPath(loadedFileName);
			printItem.PrintItemCollectionID = libraryToAddTo.baseLibraryCollection.Id;
			printItem.Commit();

			if (MeshFileIo.ValidFileExtensions().Contains(extension))
			{
				List<MeshGroup> meshToConvertAndSave = MeshFileIo.Load(loadedFileName);

				try
				{
					PrintItemWrapper printItemWrapper = new PrintItemWrapper(printItem);
					SaveToLibraryFolder(printItemWrapper, meshToConvertAndSave, false);
					libraryToAddTo.AddItem(printItemWrapper);
				}
				catch (System.UnauthorizedAccessException)
				{
					UiThread.RunOnIdle(() =>
					{
						//Do something special when unauthorized?
						StyledMessageBox.ShowMessageBox(null, "Oops! Unable to save changes, unauthorized access", "Unable to save");
					});
				}
				catch
				{
					UiThread.RunOnIdle(() =>
					{
						StyledMessageBox.ShowMessageBox(null, "Oops! Unable to save changes.", "Unable to save");
					});
				}
			}
			else // it is not a mesh so just add it
			{
				PrintItemWrapper printItemWrapper = new PrintItemWrapper(printItem);
				if (false)
				{
					libraryToAddTo.AddItem(printItemWrapper);
				}
				else // save a copy to the library and update this to point at it
				{
					string sourceFileName = printItem.FileLocation;
					string newFileName = Path.ChangeExtension(Path.GetRandomFileName(), Path.GetExtension(printItem.FileLocation));
					string destFileName = Path.Combine(ApplicationDataStorage.Instance.ApplicationLibraryDataPath, newFileName);

					File.Copy(sourceFileName, destFileName, true);

					printItemWrapper.FileLocation = destFileName;
					printItemWrapper.PrintItem.Commit();

					// let the queue know that the item has changed so it load the correct part
					libraryToAddTo.AddItem(printItemWrapper);
				}
			}
		}

		private IEnumerable<PrintItemCollection> GetChildCollections()
		{
			string query = string.Format("SELECT * FROM PrintItemCollection WHERE ParentCollectionID = {0} ORDER BY Name ASC;", baseLibraryCollection.Id);
			IEnumerable<PrintItemCollection> result = (IEnumerable<PrintItemCollection>)Datastore.Instance.dbSQLite.Query<PrintItemCollection>(query);
			return result;
		}

		private IEnumerable<PrintItem> GetLibraryItems(string keyphrase = null)
		{
			string query;
			if (keyphrase == null)
			{
				query = string.Format("SELECT * FROM PrintItem WHERE PrintItemCollectionID = {0} ORDER BY Name ASC;", baseLibraryCollection.Id);
			}
			else
			{
				query = string.Format("SELECT * FROM PrintItem WHERE PrintItemCollectionID = {0} AND Name LIKE '%{1}%' ORDER BY Name ASC;", baseLibraryCollection.Id, keyphrase);
			}
			IEnumerable<PrintItem> result = (IEnumerable<PrintItem>)Datastore.Instance.dbSQLite.Query<PrintItem>(query);
			return result;
		}

		private void loadFilesIntoLibraryBackgoundWorker_DoWork(IList<string> fileList)
		{
			foreach (string loadedFileName in fileList)
			{
				string extension = Path.GetExtension(loadedFileName).ToUpper();
				if (MeshFileIo.ValidFileExtensions().Contains(extension)
					|| extension == ".GCODE"
					|| extension == ".ZIP")
				{
					if (extension == ".ZIP")
					{
						ProjectFileHandler project = new ProjectFileHandler(null);
						List<PrintItem> partFiles = project.ImportFromProjectArchive(loadedFileName);
						if (partFiles != null)
						{
							foreach (PrintItem part in partFiles)
							{
								AddStlOrGcode(this, part.FileLocation, Path.GetExtension(part.FileLocation).ToUpper());
							}
						}
					}
					else
					{
						AddStlOrGcode(this, loadedFileName, extension);
					}
				}
			}
		}
	}
}