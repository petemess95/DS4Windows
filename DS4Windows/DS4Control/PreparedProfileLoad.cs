using System;
using System.IO;
using System.Security;
using System.Threading;
using System.Xml;
using System.Xml.Serialization;
using DS4WinWPF.DS4Control.DTOXml;

namespace DS4Windows.DS4Control
{
    internal enum ProfilePreparationFailure
    {
        None,
        Missing,
        Unreadable,
        Invalid,
    }

    /// <summary>
    /// Cold, single-use profile snapshot. Preparation exercises the canonical
    /// mapper against a private store, before any live reset or device work.
    /// This is content validation, not authority to replace a running profile:
    /// callers must still serialize apply and validate their slot/revision.
    /// </summary>
    internal sealed class PreparedProfileLoad
    {
        private ProfileDTO dto;
        private Action deferredPostLoad;

        private PreparedProfileLoad(string path, int device, ProfileDTO dto,
            bool migrated)
        {
            Path = path;
            Device = device;
            this.dto = dto;
            Migrated = migrated;
        }

        internal string Path { get; }
        internal int Device { get; }
        internal bool Migrated { get; }

        // Filled only by application, never preparation. Exact output work
        // must not enter a legacy event queue while its outer action lease is
        // still held: the base input loop drains that queue even when paused.
        internal void StagePostLoad(Action enqueue) => deferredPostLoad = enqueue;

        internal void QueuePostLoadAfterResume() =>
            Interlocked.Exchange(ref deferredPostLoad, null)?.Invoke();

        // Aim layer borrowed from another profile, built here so the source
        // profile is read before any report pause. Null = layer off.
        internal AimLayerPreparation AimLayer { get; private set; }

        internal static bool TryPrepare(string path, int device,
            out PreparedProfileLoad prepared, out ProfilePreparationFailure failure,
            out string error)
        {
            prepared = null;
            // Read before the file so a save that lands after this point is
            // known to be newer than the borrowed settings.
            long saveSequence = AimLayerState.ReadSaveSequence();
            if (!TryPrepareCandidate(path, device, out ProfileDTO candidate,
                    out BackingStore validation, out bool migrated, out failure, out error))
                return false;
            prepared = new PreparedProfileLoad(path, device, candidate, migrated)
            {
                AimLayer = AimLayerState.Prepare(validation, device, path, saveSequence),
            };
            return true;
        }

        // Prepares an aim-layer source: same validation as TryPrepare, but the
        // mapped scratch store is returned and its own aim layer is ignored.
        internal static bool TryPrepareSource(string path, int device,
            out BackingStore store, out ProfilePreparationFailure failure,
            out string error) =>
            TryPrepareCandidate(path, device, out _, out store, out _,
                out failure, out error);

        private static bool TryPrepareCandidate(string path, int device,
            out ProfileDTO prepared, out BackingStore validation, out bool migrated,
            out ProfilePreparationFailure failure, out string error)
        {
            prepared = null;
            validation = null;
            migrated = false;
            failure = ProfilePreparationFailure.None;
            error = null;
            if ((uint)device >= Global.TEST_PROFILE_ITEM_COUNT)
                throw new ArgumentOutOfRangeException(nameof(device));

            try
            {
                string xml;
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read))
                {
                    var migration = new ProfileMigration(stream);
                    try
                    {
                        // Legacy migrations replace the root element. Verify the
                        // original document first, so arbitrary XML cannot become
                        // an apparently valid empty/default profile.
                        XmlReader original = migration.ProfileReader;
                        if (original == null || original.MoveToContent() != XmlNodeType.Element ||
                            original.LocalName != "DS4Windows" || original.NamespaceURI.Length != 0)
                            throw new XmlException("Expected a DS4Windows profile document.");
                        migrated = migration.RequiresMigration();
                        if (migrated)
                            migration.Migrate();
                        xml = migration.CurrentMigrationText;
                    }
                    finally
                    {
                        migration.Close();
                    }
                }

                var serializer = ProfileDTO.Serializer;
                using var reader = new StringReader(xml);
                var candidate = serializer.Deserialize(reader) as ProfileDTO;
                if (candidate == null)
                    throw new InvalidOperationException("The document does not contain a profile.");
                candidate.DeviceIndex = device;

                // Deserialization alone does not parse colors/macros or exercise
                // mapping conversions. Do not duplicate those rules in a validator.
                // Never ResetProfile on the shadow: that has live Mapping effects.
                BackingStore store = BackingStore.CreateProfileValidationStore();
                candidate.MapTo(store);
                prepared = candidate;
                validation = store;
                return true;
            }
            catch (Exception ex) when (ex is FileNotFoundException ||
                ex is DirectoryNotFoundException)
            {
                failure = ProfilePreparationFailure.Missing;
                error = ex.Message;
            }
            catch (Exception ex) when (ex is IOException ||
                ex is UnauthorizedAccessException || ex is SecurityException)
            {
                failure = ProfilePreparationFailure.Unreadable;
                error = ex.Message;
            }
            catch (Exception ex) when (ex is XmlException ||
                ex is InvalidOperationException || ex is FormatException ||
                ex is OverflowException || ex is ArgumentException)
            {
                failure = ProfilePreparationFailure.Invalid;
                error = ex.InnerException?.Message ?? ex.Message;
            }
            migrated = false;
            return false;
        }

        internal void ApplyTo(BackingStore destination)
        {
            ArgumentNullException.ThrowIfNull(destination);
            Claim().MapTo(destination);
        }

        // Destructive callers must claim before resetting any live state. A
        // consumed candidate is a programming error, not permission to reset.
        internal ProfileDTO Claim()
        {
            ProfileDTO candidate = Interlocked.Exchange(ref dto, null);
            if (candidate == null)
                throw new InvalidOperationException("The prepared profile has already been consumed.");
            return candidate;
        }
    }
}
