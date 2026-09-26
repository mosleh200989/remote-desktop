import { Router } from "express";
import rateLimit from "express-rate-limit";
import { z } from "zod";
import { registerDevice, checkAndConsumeRegistrationRateLimit } from "../pairing";
import { env } from "../env";

export const devicesRouter = Router();

const registerLimiter = rateLimit({
  windowMs: 60_000,
  limit: 10,
  standardHeaders: true,
  legacyHeaders: false,
  message: { error: "Too many device registrations from this address, please wait a minute" },
});

const registerSchema = z.object({
  name: z.string().min(1).max(64),
});

// No accounts (AnyDesk-style): anyone can register a device anonymously and
// get back a device ID + a private host token, shown once. The server only
// ever stores a bcrypt hash of that token. The pairing code + the host's own
// explicit accept/reject are what actually authorizes a session, not this.
devicesRouter.post("/", registerLimiter, async (req, res) => {
  const parsed = registerSchema.safeParse(req.body);
  if (!parsed.success) return res.status(400).json({ error: "Invalid device name" });

  const ip = req.ip ?? "unknown";
  if (!checkAndConsumeRegistrationRateLimit(ip)) {
    return res.status(429).json({ error: "Too many devices registered from this address today" });
  }

  const { device, hostToken } = await registerDevice(parsed.data.name);
  res.status(201).json({
    deviceId: device.device_id,
    hostToken,
    name: device.name,
  });
});

devicesRouter.get("/ice-servers", (_req, res) => {
  res.json({ iceServers: env.iceServers });
});
