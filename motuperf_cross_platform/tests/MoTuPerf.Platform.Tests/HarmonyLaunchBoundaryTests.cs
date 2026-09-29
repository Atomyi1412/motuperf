using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CSharpIosPerfMonitor;
using Xunit;

namespace MoTuPerf.Platform.Tests
{
    public sealed class HarmonyLaunchBoundaryTests
    {
        private const string BackgroundBundle = "{\"bundleName\":\"com.example.background\",\"userId\":100,"
            + "\"hapModuleInfos\":[{\"moduleName\":\"service\","
            + "\"serviceAbilityInfos\":[{\"abilityName\":\"SyncService\"}]}]}";

        private static string Format(string json, bool indented)
        {
            using var document = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions { WriteIndented = indented });
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void GenericAbilityNameInServiceCollectionStaysNonUi(bool indented)
        {
            string output = Format(BackgroundBundle, indented);
            var entry = Assert.Single(HarmonyLookupService.ParseLaunchEntryPoints(output));
            Assert.Equal("service", entry.Module);
            Assert.Equal("SyncService", entry.Ability);
            Assert.False(entry.IsUiEntry);
            var app = Assert.Single(HarmonyLookupService.ParseApps(output));
            Assert.False(app.HasLaunchEntry, string.Join(";", app.HarmonyLaunchEntries.Select(entry => entry.Ability + ":" + entry.IsUiEntry)));
            Assert.True(app.HasNonUiLaunchEntry, string.Join(";", app.HarmonyLaunchEntries.Select(entry => entry.Ability + ":" + entry.IsUiEntry)));
        }

        [Theory]
        [InlineData("workerAbilities")]
        [InlineData("workerAbilityInfos")]
        public void WorkerAbilityJsonCollectionsStayNonUi(string collectionName)
        {
            string json = "{\"bundleName\":\"com.example.worker\",\"moduleName\":\"entry\",\""
                + collectionName + "\":[{\"abilityName\":\"BackgroundWorker\"}]}";

            foreach (bool indented in new[] { false, true })
            {
                List<HarmonyLaunchEntryPoint> entries = HarmonyLookupService.ParseLaunchEntryPoints(Format(json, indented));
                var entry = Assert.Single(entries);
                Assert.Equal("entry", entry.Module);
                Assert.Equal("BackgroundWorker", entry.Ability);
                Assert.False(entry.IsUiEntry);

                AppInfo app = Assert.Single(HarmonyLookupService.ParseApps(Format(json, indented)));
                Assert.False(app.HasLaunchEntry, string.Join(";", app.HarmonyLaunchEntries.Select(item => item.Ability + ":" + item.IsUiEntry)));
                Assert.True(app.HasNonUiLaunchEntry);
            }
        }

        [Theory]
        [InlineData("workerAbilities")]
        [InlineData("workerAbilityInfos")]
        public void WorkerAbilityIndentedCollectionsStayNonUi(string collectionName)
        {
            string output = "bundleName: com.example.worker\nmoduleName: entry\n"
                + collectionName + ":\n  - abilityName: BackgroundWorker\n";

            var entry = Assert.Single(HarmonyLookupService.ParseLaunchEntryPoints(output));
            Assert.Equal("entry", entry.Module);
            Assert.Equal("BackgroundWorker", entry.Ability);
            Assert.False(entry.IsUiEntry);

            AppInfo app = Assert.Single(HarmonyLookupService.ParseApps(output));
            Assert.False(app.HasLaunchEntry);
            Assert.True(app.HasNonUiLaunchEntry);
        }

        [Theory]
        [InlineData("inputMethodAbilityInfos")]
        [InlineData("accessibilityAbilityInfos")]
        [InlineData("shareAbilityInfos")]
        [InlineData("fileShareAbilityInfos")]
        [InlineData("backupAbilityInfos")]
        [InlineData("pushAbilityInfos")]
        [InlineData("vpnAbilityInfos")]
        [InlineData("wallpaperAbilityInfos")]
        [InlineData("photoEditorAbilityInfos")]
        [InlineData("printAbilityInfos")]
        [InlineData("staticSubscriberAbilityInfos")]
        [InlineData("remoteObjectAbilityInfos")]
        [InlineData("windowAbilityInfos")]
        [InlineData("embeddedUIAbilityInfos")]
        public void KnownExtensionAbilityCollectionsStayNonUiAcrossJsonAndIndentedText(string collectionName)
        {
            string json = "{\"bundleName\":\"com.example.extension\",\"moduleName\":\"entry\",\""
                + collectionName + "\":[{\"abilityName\":\"BackgroundAbility\"}]}";

            foreach (bool indented in new[] { false, true })
            {
                string output = Format(json, indented);
                HarmonyLaunchEntryPoint entry = Assert.Single(HarmonyLookupService.ParseLaunchEntryPoints(output));
                Assert.Equal("entry", entry.Module);
                Assert.Equal("BackgroundAbility", entry.Ability);
                Assert.False(entry.IsUiEntry);

                AppInfo app = Assert.Single(HarmonyLookupService.ParseApps(output));
                Assert.False(app.HasLaunchEntry, collectionName);
                Assert.True(app.HasNonUiLaunchEntry, collectionName);
            }

            string text = "bundleName: com.example.extension\nmoduleName: entry\n"
                + collectionName + ":\n  - abilityName: BackgroundAbility\n";
            HarmonyLaunchEntryPoint textEntry = Assert.Single(HarmonyLookupService.ParseLaunchEntryPoints(text));
            Assert.False(textEntry.IsUiEntry);
            AppInfo textApp = Assert.Single(HarmonyLookupService.ParseApps(text));
            Assert.False(textApp.HasLaunchEntry, collectionName);
            Assert.True(textApp.HasNonUiLaunchEntry, collectionName);
        }

        [Theory]
        [InlineData("extensionInfo")]
        [InlineData("extensionInfos")]
        [InlineData("extensionInfoList")]
        [InlineData("insightIntentUIAbilityInfos")]
        [InlineData("fenceAbilityInfos")]
        [InlineData("callerInfoQueryAbilityInfos")]
        [InlineData("assetAccelerationAbilityInfos")]
        [InlineData("formEditAbilityInfos")]
        [InlineData("distributedAbilityInfos")]
        [InlineData("appServiceAbilityInfos")]
        [InlineData("liveFormAbilityInfos")]
        [InlineData("selectionAbilityInfos")]
        [InlineData("webNativeMessagingAbilityInfos")]
        [InlineData("faultLogAbilityInfos")]
        [InlineData("notificationSubscriberAbilityInfos")]
        [InlineData("cryptoAbilityInfos")]
        [InlineData("partnerAgentAbilityInfos")]
        [InlineData("agentAbilityInfos")]
        [InlineData("agentUIAbilityInfos")]
        [InlineData("modularObjectAbilityInfos")]
        [InlineData("ukeyAuthAbilityInfos")]
        [InlineData("statusBarViewAbilityInfos")]
        [InlineData("autoFillPasswordAbilityInfos")]
        [InlineData("appAccountAuthorizationAbilityInfos")]
        [InlineData("uiAbilityInfos")]
        [InlineData("remoteNotificationAbilityInfos")]
        [InlineData("remoteLocationAbilityInfos")]
        [InlineData("voipAbilityInfos")]
        [InlineData("accountLogoutAbilityInfos")]
        [InlineData("hmsAccountAbilityInfos")]
        [InlineData("adsAbilityInfos")]
        [InlineData("liveViewLockScreenAbilityInfos")]
        [InlineData("liveViewCardAbilityInfos")]
        [InlineData("uiServiceAbilityInfos")]
        [InlineData("assetCacheAbilityInfos")]
        [InlineData("sysDialogUserAuthAbilityInfos")]
        [InlineData("sysDialogCommonAbilityInfos")]
        [InlineData("sysDialogAtomicServicePanelAbilityInfos")]
        [InlineData("sysDialogPowerAbilityInfos")]
        [InlineData("sysDialogMeetimeCallAbilityInfos")]
        [InlineData("sysDialogMeetimeContactAbilityInfos")]
        [InlineData("sysDialogMeetimeMessageAbilityInfos")]
        [InlineData("sysDialogPrintAbilityInfos")]
        [InlineData("sysPickerMediaControlAbilityInfos")]
        [InlineData("sysPickerShareAbilityInfos")]
        [InlineData("sysPickerMeetimeContactAbilityInfos")]
        [InlineData("sysPickerMeetimeCallLogAbilityInfos")]
        [InlineData("sysPickerPhotoPickerAbilityInfos")]
        [InlineData("sysPickerCameraAbilityInfos")]
        [InlineData("sysPickerNavigationAbilityInfos")]
        [InlineData("sysPickerAppSelectorAbilityInfos")]
        [InlineData("sysPickerFilePickerAbilityInfos")]
        [InlineData("sysPickerAudioPickerAbilityInfos")]
        [InlineData("sysCommonUIAbilityInfos")]
        [InlineData("autoFillSmartAbilityInfos")]
        [InlineData("sysPickerPhotoEditorAbilityInfos")]
        [InlineData("sysVisualAbilityInfos")]
        [InlineData("recentPhotoAbilityInfos")]
        [InlineData("awcWebpageAbilityInfos")]
        [InlineData("awcNewsfeedAbilityInfos")]
        [InlineData("embeddedCashierAbilityInfos")]
        [InlineData("contentEmbedAbilityInfos")]
        public void OfficialExtensionAbilityCollectionsStayNonUiAcrossJsonAndIndentedText(string collectionName)
        {
            string json = "{\"bundleName\":\"com.example.officialextension\",\"moduleName\":\"entry\",\""
                + collectionName + "\":[{\"name\":\"BackgroundExtension\"}]}";

            foreach (bool indented in new[] { false, true })
            {
                string output = Format(json, indented);
                HarmonyLaunchEntryPoint entry = Assert.Single(HarmonyLookupService.ParseLaunchEntryPoints(output));
                Assert.Equal("entry", entry.Module);
                Assert.Equal("BackgroundExtension", entry.Ability);
                Assert.False(entry.IsUiEntry, collectionName);

                AppInfo app = Assert.Single(HarmonyLookupService.ParseApps(output));
                Assert.False(app.HasLaunchEntry, collectionName);
                Assert.True(app.HasNonUiLaunchEntry, collectionName);
            }

            string text = "bundleName: com.example.officialextension\nmoduleName: entry\n"
                + collectionName + ":\n  - name: BackgroundExtension\n";
            HarmonyLaunchEntryPoint textEntry = Assert.Single(HarmonyLookupService.ParseLaunchEntryPoints(text));
            Assert.False(textEntry.IsUiEntry, collectionName);
            AppInfo textApp = Assert.Single(HarmonyLookupService.ParseApps(text));
            Assert.False(textApp.HasLaunchEntry, collectionName);
            Assert.True(textApp.HasNonUiLaunchEntry, collectionName);
        }

        [Theory]
        [InlineData("2", "1")]
        [InlineData("SERVICE", "PAGE")]
        public void ExplicitAbilityTypeClassifiesGenericAbilityInfoEntries(string serviceType, string pageType)
        {
            string json = "{\"bundleName\":\"com.example.typed\",\"hapModuleInfos\":[{"
                + "\"moduleName\":\"entry\",\"abilityInfos\":["
                + "{\"name\":\"SyncService\",\"type\":\"" + serviceType + "\"},"
                + "{\"name\":\"MainAbility\",\"type\":\"" + pageType + "\"}]}]}";

            List<HarmonyLaunchEntryPoint> entries = HarmonyLookupService.ParseLaunchEntryPoints(json);

            Assert.False(Assert.Single(entries, entry => entry.Ability == "SyncService").IsUiEntry);
            Assert.True(Assert.Single(entries, entry => entry.Ability == "MainAbility").IsUiEntry);
        }

        [Theory]
        [InlineData("2", "1")]
        [InlineData("SERVICE", "PAGE")]
        [InlineData("3", "1")]
        [InlineData("DATA", "PAGE")]
        [InlineData("4", "1")]
        [InlineData("FORM", "PAGE")]
        [InlineData("5", "1")]
        [InlineData("EXTENSION", "PAGE")]
        public void IndentedTextExplicitAbilityTypeClassifiesGenericAbilityInfoEntries(string serviceType, string pageType)
        {
            string output = "bundleName: com.example.texttyped\nmoduleName: entry\nabilityInfos:\n"
                + "  - name: SyncService\n    type: " + serviceType + "\n"
                + "  - name: MainAbility\n    type: " + pageType + "\n";

            List<HarmonyLaunchEntryPoint> entries = HarmonyLookupService.ParseLaunchEntryPoints(output);

            Assert.False(Assert.Single(entries, entry => entry.Ability == "SyncService").IsUiEntry);
            Assert.True(Assert.Single(entries, entry => entry.Ability == "MainAbility").IsUiEntry);
        }

        [Theory]
        [InlineData("0")]
        [InlineData("6")]
        [InlineData("22")]
        [InlineData("40")]
        [InlineData("255")]
        [InlineData("256")]
        [InlineData("257")]
        [InlineData("266")]
        [InlineData("269")]
        [InlineData("270")]
        [InlineData("300")]
        [InlineData("307")]
        [InlineData("400")]
        [InlineData("409")]
        [InlineData("500")]
        [InlineData("511")]
        [InlineData("INSIGHT_INTENT_UI")]
        [InlineData("AGENT_UI")]
        [InlineData("UKEY_AUTH")]
        [InlineData("HMS_ACCOUNT")]
        [InlineData("SYSDIALOG_USERAUTH")]
        [InlineData("SYSPICKER_FILEPICKER")]
        [InlineData("CONTENT_EMBED")]
        public void OfficialExtensionAbilityTypeValuesClassifyGenericAbilityInfoAsNonUi(string type)
        {
            string json = "{\"bundleName\":\"com.example.typedextension\",\"moduleName\":\"entry\",\"abilityInfos\":["
                + "{\"name\":\"BackgroundExtension\",\"type\":\"" + type + "\"}]}";
            HarmonyLaunchEntryPoint jsonEntry = Assert.Single(HarmonyLookupService.ParseLaunchEntryPoints(json));
            Assert.False(jsonEntry.IsUiEntry, type);

            string text = "bundleName: com.example.typedextension\nmoduleName: entry\nabilityInfos:\n"
                + "  - name: BackgroundExtension\n    type: " + type + "\n";
            HarmonyLaunchEntryPoint textEntry = Assert.Single(HarmonyLookupService.ParseLaunchEntryPoints(text));
            Assert.False(textEntry.IsUiEntry, type);
        }

        [Theory]
        [InlineData("sysDialog/common")]
        [InlineData("sysPicker/filePicker")]
        [InlineData("autoFill/password")]
        [InlineData("contentEmbed")]
        public void ExtensionTypeNameFieldsClassifyGenericAbilityInfoAsNonUi(string typeName)
        {
            string json = "{\"bundleName\":\"com.example.extensiontypename\",\"moduleName\":\"entry\",\"abilityInfos\":["
                + "{\"name\":\"NamedExtension\",\"extensionTypeName\":\"" + typeName + "\"}]}";
            HarmonyLaunchEntryPoint jsonEntry = Assert.Single(HarmonyLookupService.ParseLaunchEntryPoints(json));
            Assert.False(jsonEntry.IsUiEntry, typeName);

            string text = "bundleName: com.example.extensiontypename\nmoduleName: entry\nabilityInfos:\n"
                + "  - name: NamedExtension\n    extensionTypeName: " + typeName + "\n";
            HarmonyLaunchEntryPoint textEntry = Assert.Single(HarmonyLookupService.ParseLaunchEntryPoints(text));
            Assert.False(textEntry.IsUiEntry, typeName);
        }

        [Fact]
        public void OfficialExtensionAbilityTypeNamesClassifyGenericAbilityInfoAsNonUi()
        {
            string[] typeNames =
            {
                "FORM", "WORK_SCHEDULER", "INPUTMETHOD", "SERVICE", "ACCESSIBILITY",
                "DATASHARE", "FILESHARE", "STATICSUBSCRIBER", "WALLPAPER", "BACKUP",
                "WINDOW", "ENTERPRISE_ADMIN", "FILEACCESS_EXTENSION", "THUMBNAIL", "PREVIEW",
                "PRINT", "SHARE", "PUSH", "VPN", "DRIVER", "ACTION", "ADS_SERVICE",
                "EMBEDDED_UI", "INSIGHT_INTENT_UI", "PHOTO_EDITOR", "FENCE", "CALLER_INFO_QUERY",
                "ASSET_ACCELERATION", "FORM_EDIT", "DISTRIBUTED", "APP_SERVICE", "LIVE_FORM",
                "SELECTION", "WEB_NATIVE_MESSAGING", "FAULT_LOG", "NOTIFICATION_SUBSCRIBER",
                "CRYPTO", "PARTNER_AGENT", "AGENT", "AGENT_UI", "MODULAR_OBJECT", "UKEY_AUTH",
                "UI", "HMS_ACCOUNT", "APP_ACCOUNT_AUTHORIZATION", "ADS", "REMOTE_NOTIFICATION",
                "REMOTE_LOCATION", "VOIP", "ACCOUNTLOGOUT", "STATUS_BAR_VIEW", "LIVEVIEW_LOCKSCREEN",
                "LIVEVIEW_CARD", "UI_SERVICE", "ASSET_CACHE", "SYSDIALOG_USERAUTH", "SYSDIALOG_COMMON",
                "SYSDIALOG_ATOMICSERVICEPANEL", "SYSDIALOG_POWER", "SYSDIALOG_MEETIMECALL",
                "SYSDIALOG_MEETIMECONTACT", "SYSDIALOG_MEETIMEMESSAGE", "SYSDIALOG_PRINT",
                "SYSPICKER_MEDIACONTROL", "SYSPICKER_SHARE", "SYSPICKER_MEETIMECONTACT",
                "SYSPICKER_MEETIMECALLLOG", "SYSPICKER_PHOTOPICKER", "SYSPICKER_CAMERA",
                "SYSPICKER_NAVIGATION", "SYSPICKER_APPSELECTOR", "SYSPICKER_FILEPICKER",
                "SYSPICKER_AUDIOPICKER", "SYS_COMMON_UI", "AUTO_FILL_PASSWORD", "AUTO_FILL_SMART",
                "SYSPICKER_PHOTOEDITOR", "SYS_VISUAL", "RECENT_PHOTO", "AWC_WEBPAGE", "AWC_NEWSFEED",
                "EMBEDDED_CASHIER", "CONTENT_EMBED"
            };

            foreach (string typeName in typeNames)
            {
                string json = "{\"bundleName\":\"com.example.officialextensions\",\"moduleName\":\"entry\",\"abilityInfos\":["
                    + "{\"name\":\"NamedExtension\",\"extensionTypeName\":\"" + typeName + "\"}]}";
                HarmonyLaunchEntryPoint jsonEntry = Assert.Single(HarmonyLookupService.ParseLaunchEntryPoints(json));
                Assert.False(jsonEntry.IsUiEntry, typeName);

                string text = "bundleName: com.example.officialextensions\nmoduleName: entry\nabilityInfos:\n"
                    + "  - name: NamedExtension\n    extensionTypeName: " + typeName + "\n";
                HarmonyLaunchEntryPoint textEntry = Assert.Single(HarmonyLookupService.ParseLaunchEntryPoints(text));
                Assert.False(textEntry.IsUiEntry, typeName);
            }
        }

        [Theory]
        [InlineData("extensionType")]
        [InlineData("extensionTypeName")]
        [InlineData("extensionAbilityType")]
        [InlineData("extensionAbilityTypeName")]
        public void ExtensionTypeNumericOneUsesHarmonyExtensionMeaning(string typeField)
        {
            string json = "{\"bundleName\":\"com.example.workerscheduler\",\"moduleName\":\"entry\",\"abilityInfos\":["
                + "{\"name\":\"SchedulerExtension\",\"" + typeField + "\":\"1\"}]}";
            HarmonyLaunchEntryPoint jsonEntry = Assert.Single(HarmonyLookupService.ParseLaunchEntryPoints(json));
            Assert.False(jsonEntry.IsUiEntry, typeField);

            string text = "bundleName: com.example.workerscheduler\nmoduleName: entry\nabilityInfos:\n"
                + "  - name: SchedulerExtension\n    " + typeField + ": 1\n";
            HarmonyLaunchEntryPoint textEntry = Assert.Single(HarmonyLookupService.ParseLaunchEntryPoints(text));
            Assert.False(textEntry.IsUiEntry, typeField);
        }

        [Fact]
        public void GenericAbilityTypeNumericOneRemainsPage()
        {
            string json = "{\"bundleName\":\"com.example.page\",\"moduleName\":\"entry\",\"abilityInfos\":["
                + "{\"name\":\"MainAbility\",\"type\":\"1\"}]}";
            HarmonyLaunchEntryPoint entry = Assert.Single(HarmonyLookupService.ParseLaunchEntryPoints(json));
            Assert.True(entry.IsUiEntry);
        }

        [Fact]
        public void IndentedTextAbilityTypeBeforeNameAppliesToTheSameRecord()
        {
            string output = "bundleName: com.example.texttyped\nmoduleName: entry\nabilityInfos:\n"
                + "  - type: SERVICE\n    name: SyncService\n"
                + "  - type: PAGE\n    name: MainAbility\n";

            List<HarmonyLaunchEntryPoint> entries = HarmonyLookupService.ParseLaunchEntryPoints(output);

            Assert.False(Assert.Single(entries, entry => entry.Ability == "SyncService").IsUiEntry);
            Assert.True(Assert.Single(entries, entry => entry.Ability == "MainAbility").IsUiEntry);
        }

        [Fact]
        public void NamedBundleTextExplicitAbilityTypeUpdatesAppLaunchCapability()
        {
            string output = "bundleName: com.example.texttyped\nmoduleName: entry\nabilityInfos:\n"
                + "  - name: SyncService\n    type: SERVICE\n";

            AppInfo app = Assert.Single(HarmonyLookupService.ParseApps(output));

            Assert.False(app.HasLaunchEntry);
            Assert.True(app.HasNonUiLaunchEntry);
            var entry = Assert.Single(app.HarmonyLaunchEntries);
            Assert.Equal("SyncService", entry.Ability);
            Assert.False(entry.IsUiEntry);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ModulePropertyOrderCannotCreateCrossModuleEntries(bool indented)
        {
            string json = "{\"hapModuleInfos\":["
                + "{\"mainElementName\":\"EntryAbility\",\"moduleName\":\"entry\"},"
                + "{\"abilityInfos\":[{\"abilityName\":\"FeatureAbility\"}],\"moduleName\":\"feature\"}]}";
            var entries = HarmonyLookupService.ParseLaunchEntryPoints(Format(json, indented));
            Assert.Equal(new[] { "entry/EntryAbility", "feature/FeatureAbility" },
                entries.Select(entry => entry.Module + "/" + entry.Ability).OrderBy(value => value));
        }

        [Fact]
        public void IndentedTextModuleBoundaryFlushesPreviousAbilityBeforeChangingModule()
        {
            string output = "bundleName: com.example.textmodules\n"
                + "moduleName: entry\n"
                + "abilityInfos:\n"
                + "  - name: EntryAbility\n"
                + "moduleName: feature\n"
                + "abilityInfos:\n"
                + "  - name: FeatureAbility\n";

            var entries = HarmonyLookupService.ParseLaunchEntryPoints(output);

            Assert.Equal(new[] { "entry/EntryAbility", "feature/FeatureAbility" },
                entries.Select(entry => entry.Module + "/" + entry.Ability).OrderBy(value => value));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AbilityMetadataDoesNotBecomeAnotherEntry(bool indented)
        {
            string json = "{\"moduleName\":\"entry\",\"abilityInfos\":[{\"name\":\"MainAbility\","
                + "\"metadata\":[{\"name\":\"Analytics\",\"value\":\"enabled\"}],"
                + "\"resource\":{\"name\":\"IconResource\"},"
                + "\"permissions\":[{\"name\":\"PermissionName\"}]}]}";
            var entry = Assert.Single(HarmonyLookupService.ParseLaunchEntryPoints(Format(json, indented)));
            Assert.Equal("MainAbility", entry.Ability);
            Assert.Equal("entry", entry.Module);
        }

        [Fact]
        public void SingularCollectionNameIsNotAnAbilityAndKeyedEntriesRemainAvailable()
        {
            var entries = HarmonyLookupService.ParseLaunchEntryPoints(
                "{\"moduleName\":\"entry\",\"abilityInfo\":{\"name\":\"MainAbility\"},"
                + "\"extensionAbilityInfos\":{\"SyncService\":{\"exported\":true}}}");
            Assert.Equal(2, entries.Count);
            Assert.Contains(entries, entry => entry.Ability == "MainAbility" && entry.IsUiEntry);
            Assert.Contains(entries, entry => entry.Ability == "SyncService" && !entry.IsUiEntry);
        }

        [Fact]
        public void MixedJsonAndTextRetainOnlyTheirOwnModuleContext()
        {
            string output = "moduleName: textEntry\nmainAbility: TextAbility\n" + BackgroundBundle;
            var entries = HarmonyLookupService.ParseLaunchEntryPoints(output);
            Assert.Equal(2, entries.Count);
            Assert.Contains(entries, entry => entry.Module == "textEntry" && entry.Ability == "TextAbility" && entry.IsUiEntry);
            Assert.Contains(entries, entry => entry.Module == "service" && entry.Ability == "SyncService" && !entry.IsUiEntry);
        }

        [Fact]
        public void KeyedAbilityRecordDoesNotTreatItsMetadataAsAnotherEntry()
        {
            var entry = Assert.Single(HarmonyLookupService.ParseLaunchEntryPoints(
                "{\"moduleName\":\"entry\",\"abilityInfos\":{\"MainAbility\":{"
                + "\"metadata\":[{\"name\":\"Analytics\"}],\"resource\":{\"name\":\"Icon\"}}}}"));
            Assert.Equal("MainAbility", entry.Ability);
            Assert.Equal("entry", entry.Module);
        }

        [Fact]
        public void IndentedMetadataCannotCreateEntriesOrChangeModuleOfNextAbility()
        {
            string output = "bundleName: com.example.text\nuserId: 100\n"
                + "moduleName: entry\nabilityInfos:\n  - exported: true\n    name: MainAbility\n"
                + "    metadata:\n      - name: Analytics\n        moduleName: fake\n"
                + "  - name: SettingsAbility\nserviceAbilityInfos:\n  - abilityName: SyncService\n";
            var entries = HarmonyLookupService.ParseLaunchEntryPoints(output);
            Assert.Equal(new[] { "entry/MainAbility", "entry/SettingsAbility", "entry/SyncService" },
                entries.Select(entry => entry.Module + "/" + entry.Ability).OrderBy(value => value));
            Assert.All(entries, entry => Assert.Equal("entry", entry.Module));
            Assert.Contains(entries, entry => entry.Ability == "MainAbility" && entry.IsUiEntry);
            Assert.Contains(entries, entry => entry.Ability == "SettingsAbility" && entry.IsUiEntry);
            Assert.Contains(entries, entry => entry.Ability == "SyncService" && !entry.IsUiEntry);
            var app = Assert.Single(HarmonyLookupService.ParseApps(output));
            Assert.True(app.HarmonyLaunchEntries.Count == 3,
                string.Join(", ", app.HarmonyLaunchEntries.Select(entry => entry.Module + "/" + entry.Ability + "/" + entry.IsUiEntry)));
            Assert.All(app.HarmonyLaunchEntries, entry => Assert.Equal("entry", entry.Module));
        }

        [Fact]
        public void NestedMetadataCollectionsCannotResetTextInventoryContext()
        {
            string output = "bundleName: com.example.first\nuserId: 100\nmoduleName: entry\n"
                + "abilityInfos:\n  - name: MainAbility\n    metadata:\n"
                + "      serviceAbilityInfos:\n        - name: FakeService\n"
                + "      bundleInfos:\n        - name: com.example.fake\n"
                + "      moduleName: fake\n  - className: SettingsAbility\n"
                + "bundleName: com.example.second\nuserId: 101\nmoduleName: service\n"
                + "serviceAbilityInfos:\n  - abilityName: SyncService\n";
            var entries = HarmonyLookupService.ParseLaunchEntryPoints(output);
            Assert.Equal(new[] { "entry/MainAbility", "entry/SettingsAbility", "service/SyncService" },
                entries.Select(entry => entry.Module + "/" + entry.Ability).OrderBy(value => value));
            var apps = HarmonyLookupService.ParseApps(output);
            Assert.Equal(2, apps.Count);
            var first = Assert.Single(apps, app => app.BundleId == "com.example.first");
            Assert.Equal(new[] { "MainAbility", "SettingsAbility" },
                first.HarmonyLaunchEntries.Select(entry => entry.Ability).OrderBy(value => value));
            Assert.All(first.HarmonyLaunchEntries, entry =>
            {
                Assert.Equal("entry", entry.Module);
                Assert.Equal(100, entry.HarmonyUserId);
                Assert.True(entry.IsUiEntry);
            });
            var second = Assert.Single(apps, app => app.BundleId == "com.example.second");
            var service = Assert.Single(second.HarmonyLaunchEntries);
            Assert.Equal("SyncService", service.Ability);
            Assert.Equal("service", service.Module);
            Assert.Equal(101, service.HarmonyUserId);
            Assert.False(service.IsUiEntry);
        }

        [Fact]
        public async Task MultiModuleInventoryLaunchUsesOnlyExactDiscoveredEntries()
        {
            string output = "{\"bundleName\":\"com.example.modules\",\"userId\":100,\"hapModuleInfos\":["
                + "{\"mainElementName\":\"EntryAbility\",\"moduleName\":\"entry\"},"
                + "{\"abilityInfos\":[{\"abilityName\":\"FeatureAbility\"}],\"moduleName\":\"feature\"}]}";
            var app = Assert.Single(HarmonyLookupService.ParseApps(output));
            var commands = new List<string[]>();
            var service = new HarmonyLookupService((serial, command, timeout, token) =>
            {
                commands.Add(command);
                return Task.FromResult(command.Contains("FeatureAbility") && command.Contains("feature")
                    ? new ProcessResult(0, "Ability started", "")
                    : new ProcessResult(1, "", "permission denied"));
            });
            var result = await service.LaunchAppAsync("test-hdc", app, CancellationToken.None);
            Assert.Equal(0, result.ExitCode);
            Assert.All(commands, command =>
            {
                Assert.Equal("aa", command[0]);
                Assert.Equal(100, HarmonyLookupService.CommandUserId(command));
                string ability = command[Array.IndexOf(command, "-a") + 1];
                string module = command[Array.IndexOf(command, "-m") + 1];
                Assert.Contains(module + "/" + ability, new[] { "entry/EntryAbility", "feature/FeatureAbility" });
            });
        }

        [Fact]
        public void IndentedServiceGenericAbilityFieldRetainsCollectionType()
        {
            var entry = Assert.Single(HarmonyLookupService.ParseLaunchEntryPoints(
                "moduleName: service\nserviceAbilityInfos:\n  - abilityName: SyncService\n"));
            Assert.Equal("SyncService", entry.Ability);
            Assert.False(entry.IsUiEntry);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task RejectedBackgroundBundleDoesNotFallThroughToUiOrAndroidLaunch(bool accepted)
        {
            var commands = new List<string[]>();
            var app = Assert.Single(HarmonyLookupService.ParseApps(BackgroundBundle));
            var service = new HarmonyLookupService((serial, command, timeout, token) =>
            {
                commands.Add(command);
                if (command[0] == "bm")
                    return Task.FromResult(new ProcessResult(0, BackgroundBundle, ""));
                return Task.FromResult(accepted && command.Contains("SyncService")
                    ? new ProcessResult(0, "Ability started", "")
                    : new ProcessResult(1, "", "permission denied"));
            });
            var result = await service.LaunchAppAsync("test-hdc", app, CancellationToken.None);
            Assert.Equal(accepted, result.ExitCode == 0);
            Assert.NotEmpty(commands);
            Assert.All(commands, command => Assert.Equal(100, HarmonyLookupService.CommandUserId(command)));
            Assert.All(commands.Where(command => command[0] != "bm"), command =>
            {
                Assert.Equal("aa", command[0]);
                Assert.Contains("SyncService", command);
            });
        }
    }
}
