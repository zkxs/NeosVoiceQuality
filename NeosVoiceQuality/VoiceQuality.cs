//#define DEBUG_SPAM // enable this for incredible debug spam

using CodeX;
using FrooxEngine;
using HarmonyLib;
using NeosModLoader;
using POpusCodec.Enums;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace VoiceQuality
{
    public class VoiceQuality : NeosMod
    {
        public override string Name => "VoiceQuality";
        public override string Author => "runtime";
        public override string Version => "1.1.0";
        public override string Link => "https://github.com/zkxs/NeosVoiceQuality";

        private static readonly Type USER_TYPE = typeof(User);
        private static readonly string USER_PATCH_TARGET = "PostInitializeWorker";
        private static readonly Type STREAM_TYPE = typeof(OpusStream<MonoSample>);
        private static readonly string REASON_NEW_STREAM = "new stream";
        private static readonly string REASON_EXISTING_USER = "existing user";

        // neos's default bitrate is 25000, which is classified as Super Wide Band by xiph.org (https://wiki.xiph.org/Opus_Recommended_Settings#Bandwidth_Transition_Thresholds)
        // neos clamps bitrate between 2400 and 500000
        [AutoRegisterConfigKey]
        private static readonly ModConfigurationKey<int> KEY_BITRATE = new("bitrate", "Bitrate to use for your audio stream. 25000 is the default.", () => 25000);

        [AutoRegisterConfigKey]
        private static readonly ModConfigurationKey<bool> KEY_AUTO_SET_NEW_USERS = new("autoset", "Automatically set bitrate when joining new worlds?", () => true);

        // neos's default is Voip, which is very reasonable
        [AutoRegisterConfigKey]
        private static readonly ModConfigurationKey<OpusApplicationType> KEY_APPLICATION_TYPE = new("application type", "Opus Application type. Should probably be left at Voip.", () => OpusApplicationType.Voip);

        // neos's default is 20ms, which is very reasonable and recommended by xiph.org (https://wiki.xiph.org/Opus_Recommended_Settings#Framesize_Tweaking)
        [AutoRegisterConfigKey]
        private static readonly ModConfigurationKey<Delay> KEY_DELAY = new("delay", "Opus frame size. Lower values improve latency, worsen encoding efficiency, and slightly worsen bandwidth overhead. Going below 10ms disables certain speech optimizations. 20ms is the recommended default.", () => Delay.Delay20ms);

        private static readonly ISet<ModConfigurationKey> OPUS_CONFIGS = GetOpusConfigs();

        private static ModConfiguration? config;

        // set of all configs that reflect opus settings
        private static ISet<ModConfigurationKey> GetOpusConfigs()
        {
            ModConfigurationKey[] keys = new ModConfigurationKey[] { KEY_BITRATE, KEY_APPLICATION_TYPE, KEY_DELAY };
            return new HashSet<ModConfigurationKey>(keys);
        }

        // check if a given config is an opus setting
        private static bool IsOpusConfig(ModConfigurationKey configKey)
        {
            // currently all keys that aren't the autoset bool are opus things
            // if we ever have multiple non-opus keys I'll need to switch this to set.contains() call
            return !KEY_AUTO_SET_NEW_USERS.Equals(configKey);
        }
        
        public override void DefineConfiguration(ModConfigurationDefinitionBuilder builder)
        {
            builder
                .Version(new Version(1, 0, 0))
                .AutoSave(false);
        }

        public override void OnEngineInit()
        {
            config = GetConfiguration();
            if (config == null)
            {
                Error("Could not load configuration");
                return;
            }

            config.OnThisConfigurationChanged += OnConfigurationChanged;

            Harmony harmony = new Harmony("dev.zkxs.NeosVoiceQuality");
            InstallPatch(harmony, USER_TYPE, USER_PATCH_TARGET);
        }

        // handle changes to the opus configs
        private static void OnConfigurationChanged(ConfigurationChangedEvent @event)
        {
            try
            {
                if (IsOpusConfig(@event.Key))
                {
                    // update this config on all of my local users across all worlds
                    foreach (World world in Engine.Current.WorldManager.Worlds)
                    {
                        if (!world.IsUserspace())
                        {
                            SetConfigForExistingUser(world.LocalUser, @event.Key);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Error($"Something went very wrong in OnConfigurationChanged():\n{e}");
            }
        }

        private static OpusStream<MonoSample> GetUserVoiceStream(User user)
        {
            try
            {
                return (OpusStream<MonoSample>)user.Streams.Single(stream => STREAM_TYPE.IsAssignableFrom(stream.GetType()));
            }
            catch (InvalidOperationException e)
            {
                throw new InvalidOperationException($"There was not exactly one {STREAM_TYPE} stream in user {user}", e);
            }
        }

        // runs on config change, user is already checked to make sure it's the one we want to update
        // updates only one opus config on the target users
        private static void SetConfigForExistingUser(User user, ModConfigurationKey configKey)
        {
            OpusStream<MonoSample> stream;
            try
            {
                stream = GetUserVoiceStream(user);
            }
            catch (InvalidOperationException e)
            {
                Error(e.Message);
                DebugUserStreams(user);
                return;
            }

            user.World.RunSynchronously(() => {
                UpdateSyncValue(configKey, stream, REASON_EXISTING_USER);
            });
        }

        // runs on hook, maybe sometimes? This is probably dead code. User is NOT already checked to make sure it's the one we want to update
        // updates only one opus config on the target users
        private static void SetAllConfigsForExistingUser(User user)
        {
            OpusStream<MonoSample> stream;
            try
            {
                stream = GetUserVoiceStream(user);
            }
            catch (InvalidOperationException e)
            {
                Error(e.Message);
                DebugUserStreams(user);
                return;
            }

            if (!user.IsLocalUser)
            {
                Debug("Ignoring hook-based initial user setup as they aren't the local yser");
                return;
            }

            if (user.World == null || user.World.IsUserspace())
            {
                Debug("Ignoring hook-based initial user setup as they aren't in a suitable world");
                return;
            }

            // update each config for this user
            user.World.RunSynchronously(() => {
                UpdateAllSyncValues(stream, REASON_EXISTING_USER);
            });
        }

        // runs when a new stream is added to any user
        // updates ALL opus configs on the target user
        private static void SetConfigForNewStream(Stream mysteryStream)
        {
#if DEBUG_SPAM
            Debug("HandleNewStream() call");
#endif

            // this listener is added to basically every user ever, so we need to do a bunch of filtering in here
            if (mysteryStream is OpusStream<MonoSample> stream)
            {
                if (mysteryStream.User == null || mysteryStream.World == null)
                {
                    return;
                }
                User user = mysteryStream.User;
                if (!user.IsLocalUser)
                {
                    // this is someone else... lets not touch them!
                    return;
                }
                World world = mysteryStream.World;
                if (world.IsUserspace())
                {
                    // nobody talks in userspace, so who cares about my voice config there? Not me.
                    return;
                }

                // update each config for this user
                world.RunSynchronously(() => {
                    UpdateAllSyncValues(stream, REASON_NEW_STREAM);
                });
            }
        }

        // must be run synchronously!
        private static void UpdateAllSyncValues(OpusStream<MonoSample> stream, string reason)
        {
            // I actually don't know how to iterate through these given C#'s lack of generic type erasure
            UpdateSyncValue(KEY_BITRATE, stream.BitRate, reason);
            UpdateSyncValue(KEY_APPLICATION_TYPE, stream.ApplicationType, reason);
            UpdateSyncValue(KEY_DELAY, stream.EncoderDelay, reason);
        }

        // must be run synchronously!
        private static void UpdateSyncValue(ModConfigurationKey key, OpusStream<MonoSample> stream, string reason)
        {
            // this is some real bullshit because C# doesn't have generic type erasure
            // if there's a better way of doing this, it beats me
            if (KEY_BITRATE.Equals(key))
            {
                UpdateSyncValue(KEY_BITRATE, stream.BitRate, reason);
            }
            else if (KEY_APPLICATION_TYPE.Equals(key))
            {
                UpdateSyncValue(KEY_APPLICATION_TYPE, stream.ApplicationType, reason);
            }
            else if (KEY_DELAY.Equals(key))
            {
                UpdateSyncValue(KEY_DELAY, stream.EncoderDelay, reason);
            }
            else
            {
                throw new InvalidOperationException("todo");
            }
        }

        // must be run synchronously!
        private static void UpdateSyncValue<T>(ModConfigurationKey<T> configKey, Sync<T> sync, string reason)
        {
            T? value = config!.GetValue(configKey);
            if (value == null)
            {
                Warn($"{configKey.Name} had a null value... that doesn't seem right. I'm not going to write that into a Sync.");
                return;
            }
            T oldValue = sync.Value;
            sync.Value = value;
            Msg($"set {configKey.Name} from {oldValue} to {value} in {sync.World?.Name} for {reason}");
        }

        private static void DebugUserStreams(User user)
        {
            foreach(Stream someStream in user.Streams)
            {
                Debug($"  - {someStream.GetType()}");
            }
        }

        private static void InstallPatch(Harmony harmony, Type type, string methodName)
        {
            MethodInfo method = AccessTools.Method(type, methodName);
            if (method == null)
            {
                throw new ReflectionException($"Could not find {type.Name}.{methodName}()");
            }
            MethodInfo @base = method.GetBaseDefinition();
            harmony.Patch(@base, postfix: new HarmonyMethod(typeof(HarmonyPatches), nameof(HarmonyPatches.UserInitPostfix)));
        }

        // keep those pesky harmony methods separate from my normal methods
        private static class HarmonyPatches
        {
            // postfix patch to User.PostInitializeWorker()
            public static void UserInitPostfix(object __instance, MethodBase __originalMethod)
            {
                if (USER_TYPE.Equals(__instance.GetType()))
                {
#if DEBUG_SPAM
                    Debug($"Postfix call: [{__originalMethod.DeclaringType} / {__originalMethod}] on [{__instance.GetType()}]");
#endif

                    if (__instance is User user)
                    {
                        DebugUserStreams(user);
                        if (user.StreamCount != 0)
                        {
                            // This probably can't happen. It seems like PostInitializeWorker() is called very early on in the User's lifecycle, before anything is actually intialized. Cool.
                            Warn("Handling initialized user. I'm pretty sure this is unreachable, but just in case I'm leaving it in. Let me know if you're reading this!");
                            SetAllConfigsForExistingUser(user);
                        }
                        else
                        {
                            // the user is not initialized yet, and basically everything in it is also null
                            // unfortunately this means I have to add a listener to all users, not just our local user
                            // I'll have the listener itself filter out things it should be ignoring
                            Debug($"mystery user looks new... lets try adding a listener?");
                            user.StreamAdded += SetConfigForNewStream;
                        }
                    }
                    else
                    {
                        Error("Something has gone very wrong, and an object that should have been a User was not a User");
                    }
                }
            }
        }

        private class ReflectionException : Exception
        {
            public ReflectionException(string message) : base(message) { }
        }
    }
}
