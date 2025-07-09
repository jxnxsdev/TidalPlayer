#pragma once

#include <string>
#include <iostream>

class SignalSender {
public:
    SignalSender() : signalCount(0) {}
    void sendResponseSignal(const std::string& signal);

private:
    int signalCount;
};
