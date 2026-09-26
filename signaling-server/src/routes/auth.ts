import { Router } from "express";
import rateLimit from "express-rate-limit";
import { z } from "zod";
import {
  createUser,
  getUserByEmail,
  signUserToken,
  verifyPassword,
} from "../auth";

export const authRouter = Router();

// Blunt credential-stuffing / brute force against login and mass account
// creation against register. Keyed by IP; fine for a single-instance deploy.
const authLimiter = rateLimit({
  windowMs: 60_000,
  limit: 8,
  standardHeaders: true,
  legacyHeaders: false,
  message: { error: "Too many attempts, please wait a minute and try again" },
});
authRouter.use(authLimiter);

const credentialsSchema = z.object({
  email: z.string().email(),
  password: z.string().min(10, "Password must be at least 10 characters"),
});

authRouter.post("/register", async (req, res) => {
  const parsed = credentialsSchema.safeParse(req.body);
  if (!parsed.success) {
    return res.status(400).json({ error: parsed.error.issues[0]?.message ?? "Invalid input" });
  }
  const { email, password } = parsed.data;
  if (getUserByEmail(email)) {
    return res.status(409).json({ error: "An account with that email already exists" });
  }
  const user = await createUser(email, password);
  const token = signUserToken(user);
  res.status(201).json({ token, email: user.email });
});

authRouter.post("/login", async (req, res) => {
  const parsed = credentialsSchema.safeParse(req.body);
  if (!parsed.success) {
    return res.status(400).json({ error: "Invalid email or password" });
  }
  const { email, password } = parsed.data;
  const user = getUserByEmail(email);
  // Constant-shape response whether the user exists or not, to avoid
  // leaking which emails are registered.
  const ok = user ? await verifyPassword(user, password) : false;
  if (!user || !ok) {
    return res.status(401).json({ error: "Invalid email or password" });
  }
  const token = signUserToken(user);
  res.json({ token, email: user.email });
});
