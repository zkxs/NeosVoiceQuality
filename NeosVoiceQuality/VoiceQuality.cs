//#define DEBUG_SPAM // enable this for incredible debug spam

using CodeX;
using FrooxEngine;
using HarmonyLib;
using NeosModLoader;
using System;
using System.Linq;
using System.Reflection;

namespace VoiceQuality
{
    public class VoiceQuality : NeosMod
    {
        public override string Name => "VoiceQuality";
        public override string Author => "runtime";
        public override string Version => "1.0.0";
        public override string Link => "https://github.com/zkxs/NeosVoiceQuality";

        private static readonly Type USER_TYPE = typeof(User);
        private static readonly string USER_PATCH_TARGET = "PostInitializeWorker";
        private static readonly Type STREAM_TYPE = typeof(OpusStream<MonoSample>);

        // neos's default bitrate is 25000
        // neos clamps bitrate between 2400 and 500000
        [AutoRegisterConfigKey]
        private static readonly ModConfigurationKey<int> KEY_BITRATE = new ModConfigurationKey<int>("bitrate", "Bitrate to use for your audio stream. 25000 is default.", () => 25000);

        [AutoRegisterConfigKey]
        private static readonly ModConfigurationKey<bool> KEY_AUTO_SET_NEW_USERS = new ModConfigurationKey<bool>("autoset", "Automatically set bitrate when joining new worlds?", () => true);

        private static ModConfiguration? config;

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

            Harmony harmony = new Harmony("dev.zkxs.NeosVoiceQuality");

            config.OnThisConfigurationChanged += OnConfigurationChanged;

            InstallPatch(harmony, USER_TYPE, USER_PATCH_TARGET);
        }

        // handle changes to the bitrate config
        private void OnConfigurationChanged(ConfigurationChangedEvent @event)
        {
            try
            {
                if (KEY_BITRATE.Equals(@event.Key))
                {
                    foreach (World world in Engine.Current.WorldManager.Worlds)
                    {
                        if (!world.IsUserspace())
                        {
                            HandleExistingUser(world.LocalUser);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Error($"Something went very wrong in OnConfigurationChanged():\n{e}");
            }
        }

        // runs on config change
        private static void HandleExistingUser(User user)
        {
            OpusStream<MonoSample> stream;
            try
            {
                stream = (OpusStream<MonoSample>)user.Streams.Single(stream => STREAM_TYPE.IsAssignableFrom(stream.GetType()));
            }
            catch (InvalidOperationException)
            {
                Error($"There was not exactly one {STREAM_TYPE} stream in user {user}");
                DebugUserStreams(user);
                return;
            }

            user.World.RunSynchronously(() => {
                int bitrate = config!.GetValue(KEY_BITRATE);
                stream.BitRate.Value = bitrate;
                Msg($"set bitrate to {bitrate} in {user.World?.Name} for existing user");
            });
        }

        private static void DebugUserStreams(User user)
        {
            foreach(Stream someStream in user.Streams)
                {
                Debug($"  - {someStream.GetType()}");
            }
        }

        // runs when a new stream is added to any user
        private static void HandleNewStream(Stream stream)
        {
#if DEBUG_SPAM
            Debug("HandleNewStream() call");
#endif

            if (stream is OpusStream<MonoSample> opusStream)
            {
                // okay, we're in business, but we have some checks to do first
                if (stream.User == null || stream.World == null)
                {
                    return;
                }
                User user = stream.User;
                if (!user.IsLocalUser)
                {
                    // this is someone else... lets not touch them!
                    return;
                }
                World world = stream.World;
                if (world.IsUserspace())
                {
                    // nobody talks in userspace
                    return;
                }

                world.RunSynchronously(() => {
                    int bitrate = config!.GetValue(KEY_BITRATE);
                    opusStream.BitRate.Value = bitrate;
                    Msg($"set bitrate to {bitrate} in {stream.World?.Name} for new stream");
                });
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
                            HandleExistingUser(user);
                        }
                        else
                        {
                            Debug($"mystery user looks new... lets try adding a listener?");
                            user.StreamAdded += HandleNewStream;
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
