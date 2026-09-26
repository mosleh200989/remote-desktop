import { NextFunction, Request, Response } from "express";
import { verifyUserToken } from "./auth";

export interface AuthedRequest extends Request {
  userId?: number;
  userEmail?: string;
}

export function requireAuth(req: AuthedRequest, res: Response, next: NextFunction) {
  const header = req.header("authorization") ?? "";
  const token = header.startsWith("Bearer ") ? header.slice(7) : null;
  if (!token) return res.status(401).json({ error: "Missing bearer token" });
  const payload = verifyUserToken(token);
  if (!payload) return res.status(401).json({ error: "Invalid or expired token" });
  req.userId = payload.sub;
  req.userEmail = payload.email;
  next();
}
