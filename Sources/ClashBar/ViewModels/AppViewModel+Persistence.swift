import Foundation

@MainActor
extension AppViewModel {
    func resolveSelectedConfigPath() async -> String? {
        if let path = self.applySelectedConfig(configService.selectedConfig) {
            return path
        }

        if let selectedName = defaults.string(forKey: selectedConfigKey),
           let selected = configService.availableConfigs.first(where: { $0.lastPathComponent == selectedName })
        {
            configService.selectConfig(selected)
            return self.applySelectedConfig(selected)
        }

        if let legacySelectedPath = defaults.string(forKey: legacySelectedConfigKey) {
            let legacyName = URL(fileURLWithPath: legacySelectedPath).lastPathComponent
            defaults.set(legacyName, forKey: selectedConfigKey)
            defaults.removeObject(forKey: legacySelectedConfigKey)
            if let selected = configService.availableConfigs.first(where: { $0.lastPathComponent == legacyName }) {
                configService.selectConfig(selected)
                return self.applySelectedConfig(selected)
            }
        }

        _ = configService.reloadConfigs()
        return self.applySelectedConfig(configService.selectedConfig)
    }

    private func applySelectedConfig(_ selected: URL?) -> String? {
        guard let selected else { return nil }
        let selectedPath = self.syncSelectedConfigSelection(selected)
        self.syncConfigDisplayState()
        return selectedPath
    }

    func restoreSavedConfigDirectory() {
        configService.setConfigDirectory(workingDirectoryManager.configDirectoryURL)
        if let selected = configService.selectedConfig {
            _ = self.syncSelectedConfigSelection(selected)
        }
        self.syncConfigDisplayState()
    }

    func restoreLastSuccessfulConfigIfAvailable() {
        guard let lastPath = defaults.string(forKey: lastSuccessfulConfigPathKey), !lastPath.isEmpty else { return }
        let candidate = workingDirectoryManager.currentURL(forPersistedURL: URL(fileURLWithPath: lastPath))
        if candidate.path != lastPath {
            defaults.set(candidate.path, forKey: lastSuccessfulConfigPathKey)
        }
        guard FileManager.default.fileExists(atPath: candidate.path) else { return }
        guard let matched = configService.availableConfigs.first(where: {
            $0.standardizedFileURL.resolvingSymlinksInPath().path == candidate.standardizedFileURL
                .resolvingSymlinksInPath().path
        }) else {
            return
        }
        configService.selectConfig(matched)
        _ = self.syncSelectedConfigSelection(matched)
        self.syncConfigDisplayState()
    }

    func syncConfigDisplayState() {
        configDirectoryPath = configService.configDirectory?.path ?? "-"
        availableConfigFileNames = configService.availableConfigs.map(\.lastPathComponent)
        if selectedConfigName == "-", let first = availableConfigFileNames.first {
            selectedConfigName = first
        }
        self.pruneSSIDStrategyRulesIfNeeded()
        self.pruneRemoteConfigSubscriptionsIfNeeded()
        self.refreshRemoteConfigMenuStates()
    }

    func ensureAPIClient() {
        if let apiClient {
            apiClient.updateCredentials(controller: controller, secret: controllerSecret)
        } else {
            apiClient = MihomoAPIService(controller: controller, secret: controllerSecret)
        }
    }

    func persistEditableSettingsSnapshot() {
        guard !suppressSettingsPersistence else { return }
        guard !self.isRemoteTarget else { return }
        self.encodeToDefaults(currentEditableSettingsSnapshot(), key: editableSettingsSnapshotKey)
    }

    func loadPersistedEditableSettingsSnapshot() -> EditableSettingsSnapshot? {
        self.decodeFromDefaults(EditableSettingsSnapshot.self, key: editableSettingsSnapshotKey)
    }

    func persistSystemProxyExceptions() {
        self.encodeToDefaults(self.lastSavedSystemProxyExceptions, key: systemProxyExceptionsKey)
    }

    func decodeFromDefaults<Value: Decodable>(_ type: Value.Type, key: String) -> Value? {
        guard let data = defaults.data(forKey: key) else { return nil }
        return try? JSONDecoder().decode(Value.self, from: data)
    }

    func encodeToDefaults(_ value: some Encodable, key: String) {
        guard let data = try? JSONEncoder().encode(value) else { return }
        defaults.set(data, forKey: key)
    }

    func loadPersistedSystemProxyExceptions() -> [String]? {
        guard let values = self.decodeFromDefaults([String].self, key: systemProxyExceptionsKey) else { return nil }
        return self.normalizedSystemProxyExceptionValues(values)
    }

    func loadPersistedUILanguage() -> AppLanguage {
        if let raw = defaults.string(forKey: uiLanguageKey),
           let language = AppLanguage(rawValue: raw)
        {
            return language
        }
        defaults.set(AppLanguage.zhHans.rawValue, forKey: uiLanguageKey)
        return .zhHans
    }

    func loadPersistedAppearanceMode() -> AppAppearanceMode {
        if let raw = defaults.string(forKey: appearanceModeKey),
           let mode = AppAppearanceMode(rawValue: raw)
        {
            return mode
        }
        defaults.set(AppAppearanceMode.system.rawValue, forKey: appearanceModeKey)
        return .system
    }

    func loadPersistedRemoteConfigSubscriptions() -> [String: RemoteConfigSubscription] {
        let metadataURL = self.workingDirectoryManager.remoteSubscriptionsURL
        if let data = try? Data(contentsOf: metadataURL),
           let subscriptions = try? JSONDecoder().decode([String: RemoteConfigSubscription].self, from: data)
        {
            return self.validRemoteConfigSubscriptions(subscriptions)
        }

        if let subscriptions = self.decodeFromDefaults(
            [String: RemoteConfigSubscription].self,
            key: remoteConfigSubscriptionsKey)
        {
            let result = self.validRemoteConfigSubscriptions(subscriptions)
            self.writeRemoteConfigSubscriptionsFile(result)
            return result
        }

        let v1 = self.loadPersistedRemoteConfigSources()
        guard !v1.isEmpty else { return [:] }

        var migrated: [String: RemoteConfigSubscription] = [:]
        for (fileName, urlString) in v1 {
            migrated[fileName] = RemoteConfigSubscription(
                urlString: urlString,
                autoUpdateEnabled: false,
                autoUpdateIntervalHours: RemoteConfigSubscription.defaultAutoUpdateIntervalHours,
                lastUpdateCheckAt: nil)
        }
        self.writeRemoteConfigSubscriptionsFile(migrated)
        self.encodeToDefaults(migrated, key: remoteConfigSubscriptionsKey)
        defaults.removeObject(forKey: remoteConfigSourcesKey)
        return migrated
    }

    func persistRemoteConfigSubscriptions() {
        self.writeRemoteConfigSubscriptionsFile(remoteConfigSubscriptions)
        self.encodeToDefaults(remoteConfigSubscriptions, key: remoteConfigSubscriptionsKey)
    }

    func pruneRemoteConfigSubscriptionsIfNeeded() {
        // Keep subscription metadata when a downloaded YAML is temporarily missing.
        // The user can reinstall or restore the config file without losing its source URL.
    }

    private func validRemoteConfigSubscriptions(
        _ subscriptions: [String: RemoteConfigSubscription]) -> [String: RemoteConfigSubscription]
    {
        subscriptions.reduce(into: [String: RemoteConfigSubscription]()) { result, entry in
            let (fileName, subscription) = entry
            guard let normalizedName = normalizedConfigFileName(fileName), normalizedName == fileName,
                  URL(string: subscription.urlString).map({ isSupportedRemoteConfigURL($0) }) == true
            else {
                return
            }
            result[normalizedName] = subscription
        }
    }

    private func writeRemoteConfigSubscriptionsFile(_ subscriptions: [String: RemoteConfigSubscription]) {
        do {
            try FileManager.default.createDirectory(
                at: self.workingDirectoryManager.stateDirectoryURL,
                withIntermediateDirectories: true)
            let data = try JSONEncoder().encode(subscriptions)
            try data.write(to: self.workingDirectoryManager.remoteSubscriptionsURL, options: .atomic)
        } catch {
            self.appendLog(level: "error", message: "Unable to persist remote subscription metadata: \(error.localizedDescription)")
        }
    }

    func loadPersistedRemoteConfigSources() -> [String: String] {
        guard let stored = defaults.dictionary(forKey: remoteConfigSourcesKey) as? [String: String] else {
            return [:]
        }

        var result: [String: String] = [:]
        for (fileName, urlString) in stored {
            guard let normalizedName = normalizedConfigFileName(fileName), normalizedName == fileName else { continue }
            guard let url = URL(string: urlString), isSupportedRemoteConfigURL(url) else { continue }
            result[normalizedName] = url.absoluteString
        }
        return result
    }

    func loadPersistedSSIDStrategyRules() -> [SSIDStrategyRule] {
        let rules = self.decodeFromDefaults([SSIDStrategyRule].self, key: ssidStrategyRulesKey) ?? []
        return SSIDStrategyRule.normalized(rules)
    }

    func persistSSIDStrategyRules(_ rules: [SSIDStrategyRule]) {
        self.encodeToDefaults(SSIDStrategyRule.normalized(rules), key: ssidStrategyRulesKey)
    }

    func pruneSSIDStrategyRulesIfNeeded() {
        guard self.configService.configDirectory != nil else { return }

        let validConfigNames = Set(self.availableConfigFileNames.map(\.trimmed))
        let nextRules = SSIDStrategyRule.normalized(self.ssidStrategyRules).filter {
            validConfigNames.contains($0.configFileName)
        }
        guard nextRules != self.ssidStrategyRules else { return }

        self.ssidStrategyRules = nextRules
        self.persistSSIDStrategyRules(nextRules)
    }
}

extension AppViewModel {
    func refreshLaunchAtLoginStatus() {
        launchAtLoginEnabled = self.launchAtLoginService.isEnabled
    }

    func applyLaunchAtLogin(_ enabled: Bool) {
        launchAtLoginErrorMessage = nil

        do {
            try self.launchAtLoginService.setEnabled(enabled)
            launchAtLoginEnabled = self.launchAtLoginService.isEnabled
        } catch {
            launchAtLoginEnabled = self.launchAtLoginService.isEnabled
            launchAtLoginErrorMessage = self.launchAtLoginMessage(for: error)
            appendLog(level: "error", message: tr("log.launch_at_login.toggle_failed", error.localizedDescription))
        }
    }

    private func launchAtLoginMessage(for error: Error) -> String {
        guard let launchError = error as? AppLaunchServiceError else {
            return error.localizedDescription
        }

        switch launchError {
        case .unsupportedEnvironment:
            return tr("app.launch_at_login.error.unsupported_environment")
        case .requiresApproval:
            return tr("app.launch_at_login.error.requires_approval")
        case let .registrationFailed(message):
            return tr("app.launch_at_login.error.register_failed", message)
        case let .unregistrationFailed(message):
            return tr("app.launch_at_login.error.unregister_failed", message)
        }
    }
}
