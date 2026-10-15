using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace InventoryKamera
{
    public class InventoryKamera : IDisposable
	{

		private static NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

		[JsonProperty]
		public List<Character> Characters { get; set; }

		[JsonProperty]
		public Inventory Inventory { get; set; }

        public AchievementScanResult Achievements { get; private set; }
        public AchievementCatalog AchievementCatalog { get; set; }
        private readonly CancellationTokenSource achievementCancellation = new CancellationTokenSource();
        public bool IsScanningAchievements { get; private set; }
        public Action<AchievementScanResult, AchievementCategory, int> AchievementProgress { get; set; }
        public Action<System.Drawing.Bitmap, AchievementObservation> AchievementPreview { get; set; }
        public bool HasInventoryData => Characters.Count > 0 || Inventory.Size > 0;
        [JsonIgnore]
        public bool CharacterScanComplete { get; private set; } = true;
        private int equipmentEnqueued;
        private int equipmentProcessed;
        private volatile bool reportingEquipmentProcessing;

		private readonly List<Artifact> equippedArtifacts;
		private readonly List<Weapon> equippedWeapons;
		public static Queue<OcrImageCollection> workerQueue { get; set; } = new Queue<OcrImageCollection>();
		private readonly List<Thread> ImageProcessors;

		private readonly WeaponScraper weaponScraper;
		private readonly ArtifactScraper artifactScraper;
		private readonly MaterialScraper materialScraper;

		private volatile bool b_threadCancel;
		private readonly int NumWorkers;

		public bool HasData
        {
			get { return HasInventoryData || Achievements.HasData; }
        }

		public InventoryKamera()
		{
			Characters = new List<Character>();
			Inventory = new Inventory();
            Achievements = new AchievementScanResult();
			equippedArtifacts = new List<Artifact>();
			equippedWeapons = new List<Weapon>();
			ImageProcessors = new List<Thread>();

            weaponScraper = new WeaponScraper { ItemQueued = () => Interlocked.Increment(ref equipmentEnqueued) };
            artifactScraper = new ArtifactScraper { ItemQueued = () => Interlocked.Increment(ref equipmentEnqueued) };
			materialScraper = new MaterialScraper();

			b_threadCancel = false;

            switch (Properties.Settings.Default.ScannerDelay)
            {
				case 0:
					NumWorkers = 3;
					break;
				default:
					NumWorkers = 2;
					break;
            }
			Logger.Info("Kamera initialized");
		}

		public void ResetLogging()
		{
			try
			{
				Directory.Delete("./logging/weapons", true);
				Directory.Delete("./logging/artifacts", true);
				Directory.Delete("./logging/characters", true);
				Directory.Delete("./logging/materials", true);
			}
			catch { /* Intentionally ignored: the folders may not exist and are recreated below. */ }

			try
			{
				Directory.CreateDirectory("./logging/weapons");
				Directory.CreateDirectory("./logging/artifacts");
				Directory.CreateDirectory("./logging/characters");
				Directory.CreateDirectory("./logging/materials");
                Directory.CreateDirectory("./logging/achievements");
			}
			catch (Exception ex)
			{
				Logger.Error(ex, "Could not reset the logging directory");
				throw new IOException($"Could not create the logging directory '.\\logging'. Please make sure the application directory is writable and the drive is available ({ex.Message})");
			}

			Logger.Info("Logging directory reset");
		}

		public void StopImageProcessorWorkers()
		{
			b_threadCancel = true;
			AwaitProcessors();
			workerQueue = new Queue<OcrImageCollection>();
		}

		private bool disposed;

		protected virtual void Dispose(bool disposing)
		{
			if (disposed) return;
			disposed = true;
			if (disposing)
			{
				achievementCancellation.Dispose();
			}
		}

		public void Dispose()
		{
			Dispose(disposing: true);
			GC.SuppressFinalize(this);
		}

		public void GatherData()
		{
			Logger.Info("Starting inventory scan; preparing diagnostic logs and OCR data...");
			ResetLogging();

            if (!Properties.Settings.Default.ScanWeapons && !Properties.Settings.Default.ScanArtifacts &&
                !Properties.Settings.Default.ScanCharacters && !Properties.Settings.Default.ScanCharDevItems &&
                !Properties.Settings.Default.ScanMaterials)
            {
                ScanAchievements();
                return;
            }

			GenshinProcesor.ReloadData();

			// Initize Image Processors
			for (int i = 0; i < NumWorkers; i++)
			{
				Thread processor = new Thread(ImageProcessorWorker){ IsBackground = true };
				processor.Start();
				ImageProcessors.Add(processor);
			}
			Logger.Debug("Added {count} workers", ImageProcessors.Count);

			GenshinProcesor.RestartEngines();


			// Assign Traveler's custom name
			// Assign Wanderer's custom name
			// A missing character entry (e.g. outdated characters.json) must not abort the
			// scan before it even starts.
			try
			{
				GenshinProcesor.AssignTravelerName(Properties.Settings.Default.TravelerName);
				GenshinProcesor.UpdateCharacterName("wanderer", Properties.Settings.Default.WandererName);
			}
			catch (ThreadAbortException) { throw; }
			catch (Exception ex)
			{
				UserInterface.AddError($"    [FAILURE] Could not assign custom character names: {ex.Message}. Continuing scan.");
				Logger.Debug(ex, "Custom character name assignment exception");
			}


			if (Properties.Settings.Default.ScanWeapons)
			{
				UserInterface.BeginScanPhase(ScanPhase.Weapons);
				Logger.Info("Starting weapon inventory scan");
				// Get Weapons
				Navigation.InventoryScreen();
				Navigation.SelectWeaponInventory();
				try
				{
                    weaponScraper.ScanWeapons();
				}
				catch (FormatException ex) { UserInterface.AddError("    [FAILURE] Weapon inventory scan: " + ex.Message); }
				catch (ThreadAbortException) { /* Intentionally ignored: scan is being cancelled. */ }
				catch (Exception ex)
				{
					UserInterface.AddError("    [FAILURE] Weapon inventory scan: " + ex.Message);
					Logger.Debug(ex, "Weapon inventory scan exception");
				}
				Navigation.MainMenuScreen();
				Logger.Info("Finished capturing weapon inventory cards");
			}

			if (Properties.Settings.Default.ScanArtifacts)
			{
				UserInterface.BeginScanPhase(ScanPhase.Artifacts);
				Logger.Info("Starting artifact inventory scan");

				// Get Artifacts
				Navigation.InventoryScreen();
				Navigation.SelectArtifactInventory();
				try
				{
					artifactScraper.ScanArtifacts();
				}
				catch (FormatException ex) { UserInterface.AddError("    [FAILURE] Artifact inventory scan: " + ex.Message); }
				catch (ThreadAbortException) { /* Intentionally ignored: scan is being cancelled. */ }
				catch (Exception ex)
				{
					UserInterface.AddError("    [FAILURE] Artifact inventory scan: " + ex.Message);
					Logger.Debug(ex, "Artifact inventory scan exception");
				}
				Navigation.MainMenuScreen();
				Logger.Info("Finished capturing artifact inventory cards");
			}

			workerQueue.Enqueue(new OcrImageCollection(null, "END", 0));

			if (Properties.Settings.Default.ScanCharacters)
			{
				CharacterScanComplete = false;
				UserInterface.BeginScanPhase(ScanPhase.Characters);
				Logger.Info("Starting character roster scan");
				// Get characters
				CharacterNavigation.BeginScan();
				bool characterTalentsCorrected = false;
				try
				{
					CharacterNavigation.OpenCharacterScreen("start character roster scan", true);
					CharacterScraper.ScanCharacters(Characters);
					CharacterScanComplete = CharacterScraper.LastScanComplete;
					characterTalentsCorrected = true;
					CharacterNavigation.Require(CharacterScreenState.CharacterMenu, "finish character roster scan");
				}
				catch (ThreadAbortException) { /* Intentionally ignored: scan is being cancelled. */ }
				catch (CharacterNavigationException)
				{
					if (!characterTalentsCorrected) CharacterScraper.ApplyTartagliaTalentCorrection(Characters);
					throw;
				}
				catch (Exception ex)
				{
					UserInterface.AddError("    [FAILURE] Character roster scan: " + ex.Message);
					Logger.Debug(ex, "Character roster scan exception");
				}
				finally { CharacterNavigation.EndScan(); }
				Navigation.MainMenuScreen();
				Logger.Info("Finished character roster scan: {0} characters recorded; complete={1}, skipped={2}",
					Characters.Count, CharacterScanComplete, CharacterScraper.LastSkippedCount);
			}

			// Wait for Image Processors to finish
			Logger.Debug("Waiting for remaining weapon and artifact scans...");
            if (Volatile.Read(ref equipmentProcessed) < Volatile.Read(ref equipmentEnqueued))
            {
                UserInterface.BeginScanPhase(ScanPhase.EquipmentProcessing, Volatile.Read(ref equipmentEnqueued));
                reportingEquipmentProcessing = true;
                try
                {
                    UserInterface.ReportScanProgress(ScanPhase.EquipmentProcessing,
                        Volatile.Read(ref equipmentProcessed), Volatile.Read(ref equipmentEnqueued));
                    AwaitProcessors();
                }
                finally { reportingEquipmentProcessing = false; }
                int processed = Volatile.Read(ref equipmentProcessed), enqueued = Volatile.Read(ref equipmentEnqueued);
                if (processed == enqueued)
                    UserInterface.FinishScanPhase(ScanPhase.EquipmentProcessing, processed, enqueued);
                else
                    UserInterface.ReportScanProgress(ScanPhase.EquipmentProcessing, processed, enqueued,
                        "Some queued cards were not processed");
            }
            else AwaitProcessors();
			Logger.Debug("    Equipment processing finished");

			if (Properties.Settings.Default.ScanCharacters)
			{
				// Assign Artifacts to Characters
				if (Properties.Settings.Default.ScanArtifacts)
					AssignArtifacts();
				if (Properties.Settings.Default.ScanWeapons)
					AssignWeapons();
			}

			// Scan Character Development Items
			if (Properties.Settings.Default.ScanCharDevItems)
			{
				UserInterface.BeginScanPhase(ScanPhase.CharacterDevelopmentItems);
				Logger.Info("Starting character development material scan");
				// Get Materials
				Navigation.InventoryScreen();
				Navigation.SelectCharacterDevelopmentInventory();
				try
				{
					materialScraper.SetInventoryPage(InventoryPage.CharacterDevelopmentItems);
					materialScraper.Scan_Materials(Inventory);
				}
				catch (FormatException ex) { UserInterface.AddError("    [FAILURE] Character development material scan: " + ex.Message); }
				catch (ThreadAbortException) { /* Intentionally ignored: scan is being cancelled. */ }
				catch (Exception ex)
				{
					UserInterface.AddError("    [FAILURE] Character development material scan: " + ex.Message);
					Logger.Debug(ex, "Character development material scan exception");
				}
				Navigation.MainMenuScreen();
				Logger.Info("Finished character development material scan");
			}

			// Scan Materials
			if (Properties.Settings.Default.ScanMaterials)
			{
				UserInterface.BeginScanPhase(ScanPhase.Materials);
				Logger.Info("Starting material inventory scan");
				// Get Materials
				Navigation.InventoryScreen();
				Navigation.SelectMaterialInventory();
				try
				{
					materialScraper.SetInventoryPage(InventoryPage.Materials);
					materialScraper.Scan_Materials(Inventory);
				}
				catch (FormatException ex) { UserInterface.AddError("    [FAILURE] Material inventory scan: " + ex.Message); }
				catch (ThreadAbortException) { /* Intentionally ignored: scan is being cancelled. */ }
				catch (Exception ex)
				{
					UserInterface.AddError("    [FAILURE] Material inventory scan: " + ex.Message);
					Logger.Debug(ex, "Material inventory scan exception");
				}
				Navigation.MainMenuScreen();
				Logger.Info("Finished material inventory scan");
			}
            ScanAchievements();
			Logger.Info("Inventory scan finished");
		}

        public bool RequestAchievementStop()
        {
            if (!IsScanningAchievements) return false;
            achievementCancellation.Cancel();
            return true;
        }

        private void ScanAchievements()
        {
            if (!Properties.Settings.Default.ScanAchievements) return;
            IsScanningAchievements = true;
            try
            {
                var catalog = AchievementCatalog ?? global::InventoryKamera.AchievementCatalog.Load();
                if (!string.IsNullOrWhiteSpace(catalog.Warning)) Logger.Warn(catalog.Warning);
                UserInterface.BeginScanPhase(ScanPhase.Achievements, catalog.Categories.Count);
                var scraper = new AchievementScraper(catalog) { Progress = AchievementProgress, RowPreview = AchievementPreview };
                scraper.Scan(Achievements, achievementCancellation.Token);
            }
            finally { IsScanningAchievements = false; }
        }

		private void AwaitProcessors()
		{
			while (ImageProcessors.Count > 0)
			{
				ImageProcessors.RemoveAll(process => !process.IsAlive);
			}
			b_threadCancel = false;
		}

		public void ImageProcessorWorker()
		{
			Logger.Debug("Thread #{0} priority: {1}", Thread.CurrentThread.ManagedThreadId, Thread.CurrentThread.Priority);
			while (true)
			{
				if (b_threadCancel)
				{
					workerQueue.Clear();
					break;
				}

				if (workerQueue.TryDequeue(out OcrImageCollection imageCollection))
				{
					// A single unrecognized or malformed item must never kill this worker
					// thread; log it and keep processing the rest of the queue.
					try
					{
						switch (imageCollection.Type)
						{
						case "weapon":
							if (weaponScraper.IsEnhancementMaterial(imageCollection.Bitmaps.First()))
							{
								Logger.Debug("Enhancement Material found for weapon #{weaponID}", imageCollection.Id);
								weaponScraper.StopScanning = true;
								break;
							}

							UserInterface.SetGearPictureBox(imageCollection.Bitmaps[imageCollection.Bitmaps.Count - 1]);

							// Scan as weapon
							Weapon weapon = weaponScraper.CatalogueFromBitmapsAsync(imageCollection.Bitmaps, imageCollection.Id).Result;
							UserInterface.SetGear(imageCollection.Bitmaps[imageCollection.Bitmaps.Count - 1], weapon);

							string weaponPath = $"./logging/weapons/weapon{weapon.Id}/";

							if (Properties.Settings.Default.LogScreenshots) Directory.CreateDirectory(weaponPath);

							if (weapon.IsValid())
							{
								UserInterface.IncrementWeaponCount();
								Inventory.Add(weapon);
								if (!string.IsNullOrWhiteSpace(weapon.EquippedCharacter))
									equippedWeapons.Add(weapon);
								Logger.Info("    Completed weapon #{0}: {1}, level {2}, refinement {3}{4}",
									weapon.Id, weapon.Name, weapon.Level, weapon.RefinementLevel,
									string.IsNullOrWhiteSpace(weapon.EquippedCharacter) ? "" : ", equipped by " + weapon.EquippedCharacter);
							}
							else
							{
								string error = "";
								if (!weapon.HasValidWeaponName()) error += "Invalid weapon name\n"; 
								if (!weapon.HasValidRarity()) error += "Invalid weapon rarity\n";
								if (!weapon.HasValidLevel()) error += "Invalid weapon level\n";
								if (!weapon.HasValidRefinementLevel()) error += "Invalid refinement level\n";
								if (!weapon.HasValidEquippedCharacter()) error += "Invalid equipped character\n";
								UserInterface.AddError($"    [FAILURE] Weapon #{weapon.Id} ({weapon.Name ?? "unknown"}) could not be validated and was skipped.\n        {error.TrimEnd().Replace("\n", "\n        ")}");
								Logger.Debug("Invalid weapon #{0} details: {1}", weapon.Id, weapon);
								Directory.CreateDirectory(weaponPath);
								try
								{
									using (var writer = File.CreateText(weaponPath + "log.txt"))
									{
										writer.WriteLine($"Version: {Assembly.GetExecutingAssembly().GetName().Version.ToString(3)}");
										writer.WriteLine($"Resolution: {Navigation.GetWidth()}x{Navigation.GetHeight()}");
										writer.WriteLine($"Error log:\n\t{error.Replace("\n", "\n\t")}");
									}
								}
								catch (Exception ex)
								{
									Logger.Warn(ex, "Could not write diagnostic log for weapon #{0}. Continuing...", weapon.Id);
								}
							}

                            if (!weapon.IsValid() || Properties.Settings.Default.LogScreenshots)
                            {
                                // Diagnostic saves must never kill the worker or
                                // skip the remaining captures (GDI+ generic errors,
                                // missing directories, disconnected drives).
                                try
                                {
                                    Directory.CreateDirectory(weaponPath + "name");
                                    imageCollection.Bitmaps[0].Save(weaponPath + "name/name.png");
                                    Directory.CreateDirectory(weaponPath + "rarity");
                                    imageCollection.Bitmaps[0].Save(weaponPath + "rarity/rarity.png");
                                    Directory.CreateDirectory(weaponPath + "level");
                                    imageCollection.Bitmaps[1].Save(weaponPath + "level/level.png");
                                    Directory.CreateDirectory(weaponPath + "refinement");
                                    imageCollection.Bitmaps[2].Save(weaponPath + "refinement/refinement.png");
                                    Directory.CreateDirectory(weaponPath + "equipped");
                                    imageCollection.Bitmaps[4].Save(weaponPath + "equipped/equipped.png");

                                    imageCollection.Bitmaps[imageCollection.Bitmaps.Count - 1].Save(weaponPath + "card.png");
                                }
                                catch (Exception ex)
                                {
                                    Logger.Warn(ex, "Could not save diagnostic screenshots for weapon #{0}. Continuing...", weapon.Id);
                                }
                                Task.Run(() => LogObject(weapon, weaponPath + "weapon.json"), achievementCancellation.Token);
                            }

                            // Dispose of everything
                            imageCollection.Bitmaps.ForEach(b => b.Dispose());
							break;

						case "artifact":
							if (ArtifactScraper.IsEnhancementMaterial(imageCollection.Bitmaps[imageCollection.Bitmaps.Count - 1]))
							{
								Logger.Debug("Enhancement Material found for artifact #{artifactID}", imageCollection.Id);
								artifactScraper.StopScanning = true;
								break;
							}

							UserInterface.SetGearPictureBox(imageCollection.Bitmaps[imageCollection.Bitmaps.Count - 1]);
							// Scan as artifact
							Artifact artifact = ArtifactScraper.CatalogueFromBitmapsAsync(imageCollection.Bitmaps, imageCollection.Id).Result;
							UserInterface.SetGear(imageCollection.Bitmaps[imageCollection.Bitmaps.Count - 1], artifact);

							string artifactPath = $"./logging/artifacts/artifact{artifact.Id}/";

                            if (Properties.Settings.Default.LogScreenshots) Directory.CreateDirectory(artifactPath);

							if (artifact.IsValid())
							{
								UserInterface.IncrementArtifactCount();
								Inventory.Add(artifact);
								if (!string.IsNullOrWhiteSpace(artifact.EquippedCharacter))
									equippedArtifacts.Add(artifact);
								Logger.Info("    Completed artifact #{0}: {1} {2}, level {3}, main stat {4}{5}",
									artifact.Id, artifact.SetName, artifact.GearSlot, artifact.Level, artifact.MainStat,
									string.IsNullOrWhiteSpace(artifact.EquippedCharacter) ? "" : ", equipped by " + artifact.EquippedCharacter);
							}
							else
							{
								string error = "";
								if (!artifact.HasValidSlot()) error += "Invalid artifact gear slot\n";
								if (!artifact.HasValidSetName()) error += "Invalid artifact set name\n";
								if (!artifact.HasValidRarity()) error += "Invalid artifact rarity\n";
								if (!artifact.HasValidLevel()) error += "Invalid artifact level\n";
								if (!artifact.HasValidMainStat()) error += "Invalid artifact main stat\n";
								if (!artifact.HasValidSubStats()) error += "Invalid artifact sub stats\n";
								if (!artifact.HasValidEquippedCharacter()) error += "Invalid equipped character\n";
								UserInterface.AddError($"    [FAILURE] Artifact #{artifact.Id} ({artifact.SetName ?? "unknown"}) could not be validated and was skipped.\n        {error.TrimEnd().Replace("\n", "\n        ")}");
								Logger.Debug("Invalid artifact #{0} details: {1}", artifact.Id, artifact);
								Directory.CreateDirectory(artifactPath);
								try
								{
									using (var writer = File.CreateText(artifactPath + "log.txt"))
									{
										writer.WriteLine($"Version: {Assembly.GetExecutingAssembly().GetName().Version.ToString(3)}");
										writer.WriteLine($"Resolution: {Navigation.GetWidth()}x{Navigation.GetHeight()}");
										writer.WriteLine($"Error Log:\n\t{error.Replace("\n", "\n\t")}");
									}
								}
								catch (Exception ex)
								{
									Logger.Warn(ex, "Could not write diagnostic log for artifact #{0}. Continuing...", artifact.Id);
								}
							}

                            if (!artifact.IsValid() || Properties.Settings.Default.LogScreenshots)
                            {
                                // Diagnostic saves must never kill the worker or
                                // skip the remaining captures (GDI+ generic errors,
                                // missing directories, disconnected drives).
                                try
                                {
                                    Directory.CreateDirectory(artifactPath + "name");
                                    imageCollection.Bitmaps[0].Save(artifactPath + "name/name.png");
                                    Directory.CreateDirectory(artifactPath + "slot");
									imageCollection.Bitmaps[1].Save(artifactPath + "slot/slot.png");
                                    Directory.CreateDirectory(artifactPath + "mainstat");
                                    imageCollection.Bitmaps[2].Save(artifactPath + "mainstat/mainstat.png");
									Directory.CreateDirectory(artifactPath + "level");
									imageCollection.Bitmaps[3].Save(artifactPath + "level/level.png");
									Directory.CreateDirectory(artifactPath + "substats");
									imageCollection.Bitmaps[4].Save(artifactPath + "substats/substats.png");
									Directory.CreateDirectory(artifactPath + "equipped");
									imageCollection.Bitmaps[5].Save(artifactPath + "equipped/equipped.png");
									Directory.CreateDirectory(artifactPath + "locked");
									imageCollection.Bitmaps[6].Save(artifactPath + "locked/locked.png");
									// Sanctified banner capture (index 7), for diagnosing
									// artifacts crafted with Sanctifying Elixir.
									// (Ported from taiwenlee/Inventory_Kamera (MIT): 5e4687c6)
									Directory.CreateDirectory(artifactPath + "sanctify");
									imageCollection.Bitmaps[7].Save(artifactPath + "sanctify/sanctify.png");

									imageCollection.Bitmaps[imageCollection.Bitmaps.Count - 1].Save(artifactPath + "card.png");
								}
								catch (Exception ex)
								{
									Logger.Warn(ex, "Could not save diagnostic screenshots for artifact #{0}. Continuing...", artifact.Id);
								}

								Task.Run(()=>LogObject(artifact, artifactPath + "artifact.json"), achievementCancellation.Token);
							}

							// Dispose of everything
							imageCollection.Bitmaps.ForEach(b => b.Dispose());
							break;

						case "END":
							b_threadCancel = true;
							break;

						default:
							MainForm.UnexpectedError("Unknown Image type for Image Processor");
							break;
					}
					}
					catch (Exception ex)
					{
						Logger.Error(ex, "    [FAILURE] Could not process {0} #{1}. Skipping item: {2}", imageCollection.Type, imageCollection.Id, ex.GetBaseException().Message);
						try { imageCollection.Bitmaps?.ForEach(b => b?.Dispose()); } catch { /* Intentionally ignored: best-effort cleanup during error handling. */ }
					}
                    finally
                    {
                        if (imageCollection.Type == "weapon" || imageCollection.Type == "artifact")
                        {
                            int finished = Interlocked.Increment(ref equipmentProcessed);
                            if (reportingEquipmentProcessing)
                                UserInterface.ReportScanProgress(ScanPhase.EquipmentProcessing, finished,
                                    Volatile.Read(ref equipmentEnqueued));
                        }
                    }
				}
				else
				{
					// Wait for more images to process
					Thread.Sleep(250);
				}
			}
			Logger.Debug("Thread {threadId} exit", Thread.CurrentThread.ManagedThreadId);
		}

        private static void LogObject(object obj, string path)
        {
            using (var file = new StreamWriter(path))
            {
                var serializer = new JsonSerializer
                {
                    Formatting = Formatting.Indented
                };
                serializer.Serialize(file, obj);
            }
        }

        public void AssignArtifacts()
		{
			Logger.Info("Matching scanned artifacts to characters...");
			foreach (Artifact artifact in equippedArtifacts)
			{
				foreach (Character character in Characters)
				{
					if (artifact.EquippedCharacter == character.NameGOOD)
					{
						character.AssignArtifact(artifact); // Do we even need to do this?
						Logger.Info("    Assigned {0}'s {1}: {2}, level {3}", character.NameGOOD, artifact.GearSlot, artifact.SetName, artifact.Level);
						break;
					}
				}
			}
		}

		public void AssignWeapons()
		{
			Logger.Info("Matching scanned weapons to characters...");
			foreach (Character character in Characters)
			{
				Logger.Info("    Assigning {0}'s weapon...", character.NameGOOD);
				foreach (Weapon weapon in equippedWeapons)
				{
					if (weapon.EquippedCharacter == character.NameGOOD)
					{
						character.AssignWeapon(weapon);
						Logger.Info("        {0}'s weapon: {1}, level {2}, refinement {3}", character.NameGOOD, weapon.Name, weapon.Level, weapon.RefinementLevel);
						break;
					}
				}
				if (character.Weapon is null)
				{
					Inventory.Add(new Weapon(character.WeaponType, character.NameGOOD));
					Logger.Info("        {0}: no scanned equipped weapon found; assigned the default weapon", character.NameGOOD);
				}
			}
		}
	}
}
