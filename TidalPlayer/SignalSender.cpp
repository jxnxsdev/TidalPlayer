#include "SignalSender.h"

/**
 * @brief Outputs a response signal with an incrementing count.
 * 
 * Sends a response signal string, prefixed by the internal signal count.
 * 
 * @param signal A JSON-formatted response string.
 */
void SignalSender::sendResponseSignal(const std::string& signal) {
    std::cout << std::to_string(signalCount) << ": " << signal << std::endl;
    signalCount++;
}