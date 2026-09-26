import { Router } from "express";
import { z } from "zod";
import { db } from "../db";
import { AuthedRequest, requireAuth } from "../middleware";
import { createPairingCode, getDeviceByDeviceId, registerDevice } from "../pairing";
import { env } from "../env";

export const devicesRouter = Router();
devicesRouter.use(requireAuth);

const registerSchema = z.object({
  name: z.string().min(1).max(64),
});

// Called once by the Windows host app (after the operator signs in inside the
// host app) to mint a device ID + long-lived host token. The host token is
// shown once and then stored locally by the host app; the server only ever
// keeps its bcrypt hash.
devicesRouter.post("/", async (req: AuthedRequest, res) => {
  const parsed = registerSchema.safeParse(req.body);
  if (!parsed.success) return res.status(400).json({ error: "Invalid device name" });
  const { device, hostToken } = await registerDevice(req.userId!, parsed.data.name);
  res.status(201).json({
    deviceId: device.device_id,
    hostToken,
    name: device.name,
  });
});

devicesRouter.get("/", (req: AuthedRequest, res) => {
  const rows = db
    .prepare(
      "SELECT device_id as deviceId, name, created_at as createdAt, last_seen_at as lastSeenAt FROM devices WHERE owner_user_id = ?"
    )
    .all(req.userId!);
  res.json({ devices: rows });
});

// Owner-only: mint a fresh pairing code for one of their devices. The
// controller side never calls this - codes are read off the host app's UI.
devicesRouter.post("/:deviceId/pairing-codes", (req: AuthedRequest, res) => {
  const device = getDeviceByDeviceId(req.params.deviceId);
  if (!device || device.owner_user_id !== req.userId) {
    return res.status(404).json({ error: "Device not found" });
  }
  const { code, expiresAt } = createPairingCode(device.device_id);
  res.status(201).json({ code, expiresAt });
});

devicesRouter.get("/ice-servers", (_req, res) => {
  res.json({ iceServers: env.iceServers });
});
