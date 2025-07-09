#pragma once
#include <string>
#include <unordered_map>

class SignalHandler
{
public:

    void handleSignal(const std::string& signal);
    void sendResponseSignal(const std::string& signal);

private:
    
    int signalCount = 45;
};
