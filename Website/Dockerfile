FROM node:24-bookworm-slim
WORKDIR /app
COPY package.json package-lock.json ./
RUN npm ci --omit=dev
COPY server.mjs accounts.mjs rooms.mjs game-addresses.mjs minecraft-gateway.mjs ./
COPY public ./public
RUN mkdir -p /app/data && chown -R node:node /app
USER node
ENV HOST=0.0.0.0 PORT=3210
EXPOSE 3210 25565
CMD ["node", "server.mjs"]
