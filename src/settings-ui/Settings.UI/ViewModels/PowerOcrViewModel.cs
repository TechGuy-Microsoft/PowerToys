﻿// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Timers;
using global::PowerToys.GPOWrapper;
using Microsoft.PowerToys.Settings.UI.Library;
using Microsoft.PowerToys.Settings.UI.Library.Helpers;
using Microsoft.PowerToys.Settings.UI.Library.Interfaces;
using Windows.Globalization;
using Windows.Media.Ocr;

namespace Microsoft.PowerToys.Settings.UI.ViewModels
{
    public class PowerOcrViewModel : Observable, IDisposable
    {
        private bool disposedValue;

        // Delay saving of settings in order to avoid calling save multiple times and hitting file in use exception. If there is no other request to save settings in given interval, we proceed to save it, otherwise we schedule saving it after this interval
        private const int SaveSettingsDelayInMs = 500;

        private GeneralSettings GeneralSettingsConfig { get; set; }

        private readonly ISettingsUtils _settingsUtils;
        private readonly object _delayedActionLock = new object();

        private readonly PowerOcrSettings _powerOcrSettings;
        private Timer _delayedTimer;

        private GpoRuleConfigured _enabledGpoRuleConfiguration;
        private bool _enabledStateIsGPOConfigured;
        private bool _isEnabled;
        private int _languageIndex;
        private List<Language> possibleOcrLanguages;

        public ObservableCollection<string> AvailableLanguages { get; } = new ObservableCollection<string>();

        public int LanguageIndex
        {
            get
            {
                return _languageIndex;
            }

            set
            {
                if (value != _languageIndex)
                {
                    _languageIndex = value;
                    if (_powerOcrSettings != null && _languageIndex < possibleOcrLanguages.Count && _languageIndex >= 0)
                    {
                        _powerOcrSettings.Properties.PreferredLanguage = possibleOcrLanguages[_languageIndex].DisplayName;
                        NotifySettingsChanged();
                    }

                    OnPropertyChanged(nameof(LanguageIndex));
                }
            }
        }

        private Func<string, int> SendConfigMSG { get; }

        public PowerOcrViewModel(
            ISettingsUtils settingsUtils,
            ISettingsRepository<GeneralSettings> settingsRepository,
            ISettingsRepository<PowerOcrSettings> powerOcrsettingsRepository,
            Func<string, int> ipcMSGCallBackFunc)
        {
            // To obtain the general settings configurations of PowerToys Settings.
            if (settingsRepository == null)
            {
                throw new ArgumentNullException(nameof(settingsRepository));
            }

            GeneralSettingsConfig = settingsRepository.SettingsConfig;

            // To obtain the settings configurations of Fancy zones.
            if (settingsRepository == null)
            {
                throw new ArgumentNullException(nameof(settingsRepository));
            }

            _settingsUtils = settingsUtils ?? throw new ArgumentNullException(nameof(settingsUtils));

            if (powerOcrsettingsRepository == null)
            {
                throw new ArgumentNullException(nameof(powerOcrsettingsRepository));
            }

            _powerOcrSettings = powerOcrsettingsRepository.SettingsConfig;

            _enabledGpoRuleConfiguration = GPOWrapper.GetConfiguredTextExtractorEnabledValue();
            if (_enabledGpoRuleConfiguration == GpoRuleConfigured.Disabled || _enabledGpoRuleConfiguration == GpoRuleConfigured.Enabled)
            {
                // Get the enabled state from GPO.
                _enabledStateIsGPOConfigured = true;
                _isEnabled = _enabledGpoRuleConfiguration == GpoRuleConfigured.Enabled;
            }
            else
            {
                _isEnabled = GeneralSettingsConfig.Enabled.PowerOCR;
            }

            // set the callback functions value to hangle outgoing IPC message.
            SendConfigMSG = ipcMSGCallBackFunc;

            _delayedTimer = new Timer();
            _delayedTimer.Interval = SaveSettingsDelayInMs;
            _delayedTimer.Elapsed += DelayedTimer_Tick;
            _delayedTimer.AutoReset = false;
        }

        public bool IsEnabled
        {
            get => _isEnabled;
            set
            {
                if (_enabledStateIsGPOConfigured)
                {
                    // If it's GPO configured, shouldn't be able to change this state.
                    return;
                }

                if (_isEnabled != value)
                {
                    _isEnabled = value;
                    OnPropertyChanged(nameof(IsEnabled));

                    // Set the status of PowerOCR in the general settings
                    GeneralSettingsConfig.Enabled.PowerOCR = value;
                    var outgoing = new OutGoingGeneralSettings(GeneralSettingsConfig);

                    SendConfigMSG(outgoing.ToString());
                }
            }
        }

        public bool IsEnabledGpoConfigured
        {
            get => _enabledStateIsGPOConfigured;
        }

        public HotkeySettings ActivationShortcut
        {
            get => _powerOcrSettings.Properties.ActivationShortcut;
            set
            {
                if (_powerOcrSettings.Properties.ActivationShortcut != value)
                {
                    _powerOcrSettings.Properties.ActivationShortcut = value;
                    OnPropertyChanged(nameof(ActivationShortcut));

                    _settingsUtils.SaveSettings(_powerOcrSettings.ToJsonString(), PowerOcrSettings.ModuleName);
                    NotifySettingsChanged();
                }
            }
        }

        internal void UpdateLanguages()
        {
            int preferredLanguageIndex = -1;
            int systemLanguageIndex = -1;
            CultureInfo systemCulture = CultureInfo.CurrentUICulture;

            // get the list of all installed OCR languages. While processing them, search for the previously preferred language and also for the current ui language
            possibleOcrLanguages = OcrEngine.AvailableRecognizerLanguages.OrderBy(x => x.NativeName).ToList();
            AvailableLanguages.Clear();
            foreach (Language language in possibleOcrLanguages)
            {
                if (_powerOcrSettings.Properties.PreferredLanguage?.Equals(language.DisplayName) == true)
                {
                    preferredLanguageIndex = AvailableLanguages.Count;
                }

                if (systemCulture.DisplayName.Equals(language.DisplayName) || systemCulture.Parent.DisplayName.Equals(language.DisplayName))
                {
                    systemLanguageIndex = AvailableLanguages.Count;
                }

                AvailableLanguages.Add(language.NativeName);
            }

            // if the previously stored preferred language is not available (has been deleted or this is the first run with language preference)
            if (preferredLanguageIndex == -1)
            {
                // try to use the current ui language. If it is also not available, set the first language as preferred (to have any selected language)
                if (systemLanguageIndex >= 0)
                {
                    preferredLanguageIndex = systemLanguageIndex;
                }
                else
                {
                    preferredLanguageIndex = 0;
                }
            }

            // set the language index -> the preferred language gets selected in the combo box
            LanguageIndex = preferredLanguageIndex;
        }

        private void ScheduleSavingOfSettings()
        {
            lock (_delayedActionLock)
            {
                if (_delayedTimer.Enabled)
                {
                    _delayedTimer.Stop();
                }

                _delayedTimer.Start();
            }
        }

        private void DelayedTimer_Tick(object sender, EventArgs e)
        {
            lock (_delayedActionLock)
            {
                _delayedTimer.Stop();
                NotifySettingsChanged();
            }
        }

        private void NotifySettingsChanged()
        {
            // Using InvariantCulture as this is an IPC message
            SendConfigMSG(
                   string.Format(
                       CultureInfo.InvariantCulture,
                       "{{ \"powertoys\": {{ \"{0}\": {1} }} }}",
                       PowerOcrSettings.ModuleName,
                       JsonSerializer.Serialize(_powerOcrSettings)));
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    _delayedTimer.Dispose();
                }

                disposedValue = true;
            }
        }

        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }
}
