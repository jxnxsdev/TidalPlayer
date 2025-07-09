#include "SignalHandler.h"
#include <iostream>
#include "includes/json.hpp"

enum class CommandType {
    DevicesList,
    DeviceSelect,
    MediaLoad,
    MediaPreload,
    MediaVolume,
    MediaPlay,
    MediaPause,
    MediaStop,
    MediaSeek,
    Unknown
};

static const std::unordered_map<std::string, CommandType> commandMap = {
    {"media.devices.list",   CommandType::DevicesList},
    {"media.device.select",  CommandType::DeviceSelect},
    {"media.load",           CommandType::MediaLoad},
    {"media.preload",        CommandType::MediaPreload},
    {"media.volume",         CommandType::MediaVolume},
    {"media.play",           CommandType::MediaPlay},
    {"media.pause",          CommandType::MediaPause},
    {"media.stop",           CommandType::MediaStop},
    {"media.seek",           CommandType::MediaSeek}
};

/**
 * @brief Handles incoming JSON-formatted signal commands.
 * 
 * Parses a JSON string and dispatches the contained `command` to the appropriate handler.
 * 
 * @param signal A JSON-formatted string containing a `command` key.
 */
void SignalHandler::handleSignal(const std::string& signal) {
    nlohmann::json jsonSignal;
    try {
        jsonSignal = nlohmann::json::parse(signal);
    } catch (const nlohmann::json::parse_error& e) {
        std::cerr << "Error parsing signal: " << e.what() << std::endl;
        return;
    }

    if (!jsonSignal.contains("command") || !jsonSignal["command"].is_string()) {
        std::cerr << "Invalid or missing 'command' in signal." << std::endl;
        return;
    }

    const std::string& commandStr = jsonSignal["command"];
    CommandType command = CommandType::Unknown;

    auto it = commandMap.find(commandStr);
    if (it != commandMap.end()) {
        command = it->second;
    }

    switch (command) {
        case CommandType::DevicesList:
            // TODO: handle device listing
            break;
        case CommandType::DeviceSelect:
            // TODO: handle device selection
            break;
        case CommandType::MediaLoad:
            // TODO: handle media load
            break;
        case CommandType::MediaPreload:
            // TODO: handle media preload
            break;
        case CommandType::MediaVolume:
            // TODO: handle volume change
            break;
        case CommandType::MediaPlay:
            // TODO: handle play
            break;
        case CommandType::MediaPause:
            // TODO: handle pause
            break;
        case CommandType::MediaStop:
            // TODO: handle stop
            break;
        case CommandType::MediaSeek:
            // TODO: handle seek
            break;
        case CommandType::Unknown:
        default:
            std::cerr << "Unknown command: " << commandStr << std::endl;
            break;
    }
}
